using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emutastic.Configuration;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Caching orchestrator for the Achievements tab.
    ///
    /// Built on the same Task.Run + ConfigureAwait(false) + SemaphoreSlim
    /// pattern the rest of the app uses (see ArtworkFetchService) — no
    /// dedicated dispatcher thread. The "UI never blocks" guarantee comes
    /// from every method being awaitable and every internal await using
    /// ConfigureAwait(false). Callers from the UI thread should kick these
    /// off without awaiting on the dispatcher, e.g.:
    ///
    ///   <code>
    ///   _ = Task.Run(async () =&gt;
    ///   {
    ///       var profile = await _ra.GetProfileAsync(ct);
    ///       Dispatcher.BeginInvoke(() =&gt; vm.Profile = profile);
    ///   });
    ///   </code>
    ///
    /// Cache layer is SQLite (<see cref="DatabaseService.GetRaCache"/> /
    /// <see cref="DatabaseService.SetRaCache"/>) keyed by an opaque string;
    /// owner column groups per-user payloads for wipe-on-logout. Stale-cache
    /// fallback: if the network call fails, the last-known payload is still
    /// returned so the UI shows something instead of a blank panel.
    /// </summary>
    public class RaDataService
    {
        private readonly IConfigurationService _config;
        private readonly DatabaseService _db;
        private readonly RetroAchievementsService _api;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        // ── TTLs ────────────────────────────────────────────────────────────
        // Per-panel TTLs from the plan. Public so phase implementations can
        // share constants instead of re-deciding cache freshness per call.
        public static readonly TimeSpan TtlProfile            = TimeSpan.FromHours(1);
        public static readonly TimeSpan TtlPoints             = TimeSpan.FromHours(1);
        public static readonly TimeSpan TtlRecentActivity     = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan TtlAchievementOfWeek  = TimeSpan.FromHours(24);
        public static readonly TimeSpan TtlAwards             = TimeSpan.FromHours(1);
        public static readonly TimeSpan TtlCompletionProgress = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan TtlRecentlyPlayed     = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan TtlWantToPlay         = TimeSpan.FromHours(1);
        public static readonly TimeSpan TtlRecentGameAwards   = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan TtlTopTen             = TimeSpan.FromHours(24);
        public static readonly TimeSpan TtlConsoleIds         = TimeSpan.FromDays(30);
        public static readonly TimeSpan TtlGameHashes         = TimeSpan.FromDays(30);
        public static readonly TimeSpan TtlGameList           = TimeSpan.FromDays(7);
        public static readonly TimeSpan TtlHeatmapCurrent     = TimeSpan.FromHours(1);

        public RaDataService(IConfigurationService config, DatabaseService db, RetroAchievementsService api)
        {
            _config = config;
            _db = db;
            _api = api;
        }

        /// <summary>
        /// The current RA username, or null when achievements aren't configured.
        /// Empty / missing username means "no per-user calls are possible";
        /// public endpoints (Achievement of the Week, Top Ten) still work.
        /// </summary>
        public string? CurrentUser()
        {
            var ra = _config?.GetRetroAchievementsConfiguration();
            return string.IsNullOrWhiteSpace(ra?.Username) ? null : ra.Username;
        }

        /// <summary>True when a Web API key is available; without it every per-user fetch returns null.</summary>
        public bool HasApiKey()
        {
            var ra = _config?.GetRetroAchievementsConfiguration();
            return !string.IsNullOrWhiteSpace(ra?.ApiKey);
        }

        /// <summary>
        /// Stable owner tag for a user's cached payloads. Used by
        /// <see cref="InvalidateUser"/> to wipe everything tied to one
        /// username on logout or API-key change.
        /// </summary>
        public static string OwnerForUser(string username) => $"user:{username}";

        // ── Generic cache wrapper ──────────────────────────────────────────

        /// <summary>
        /// Returns a cached payload if it's still fresh (fetched_at + ttl &gt;
        /// now); otherwise calls <paramref name="fetch"/>, persists the
        /// result, and returns it. On network failure with a stale row
        /// present, returns the stale row so the UI shows last-known instead
        /// of blank. On network failure with no row, returns null.
        /// </summary>
        public async Task<T?> GetCachedAsync<T>(
            string cacheKey,
            string owner,
            TimeSpan ttl,
            Func<CancellationToken, Task<T?>> fetch,
            CancellationToken ct = default) where T : class
        {
            DatabaseService.RaCacheRow? row = null;
            try { row = _db.GetRaCache(cacheKey); }
            catch (Exception ex) { Trace.WriteLine($"[RA] cache read failed for {cacheKey}: {ex.Message}"); }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool isFresh = row != null && row.FetchedAt > 0 && (now - row.FetchedAt) < row.TtlSeconds;

            if (isFresh && !string.IsNullOrEmpty(row!.Payload))
            {
                T? cached = Deserialize<T>(row.Payload);
                if (cached != null) return cached;
                // Cache row corrupt — fall through to refetch.
            }

            T? fresh;
            try
            {
                fresh = await fetch(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RA] cache fetch failed for {cacheKey}: {ex.Message}");
                fresh = null;
            }

            if (fresh != null)
            {
                try
                {
                    string json = JsonSerializer.Serialize(fresh, _jsonOpts);
                    _db.SetRaCache(cacheKey, owner, json, now, (long)ttl.TotalSeconds);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RA] cache write failed for {cacheKey}: {ex.Message}");
                }
                return fresh;
            }

            // Network gave us nothing — last-known stale row beats blank.
            if (row != null && !string.IsNullOrEmpty(row.Payload))
                return Deserialize<T>(row.Payload);

            return null;
        }

        /// <summary>
        /// Returns whatever's cached without checking freshness or calling
        /// the network. Used by the UI to render skeleton-replacing content
        /// instantly on cold paint, before any refresh fires.
        /// </summary>
        public T? PeekCached<T>(string cacheKey) where T : class
        {
            try
            {
                var row = _db.GetRaCache(cacheKey);
                if (row == null || string.IsNullOrEmpty(row.Payload)) return null;
                return Deserialize<T>(row.Payload);
            }
            catch { return null; }
        }

        /// <summary>
        /// Drops every cached row for the given user. Call on RA logout or
        /// when the user changes their Web API key in Preferences so the next
        /// sign-in doesn't serve the prior user's stats.
        ///
        /// Note: ra_heatmap_daily is keyed by (user, date) so logout doesn't
        /// need to wipe it — different users naturally don't see each other's
        /// rows. Past days survive across logout/login cycles intentionally
        /// so returning users see their heatmap immediately on cold open.
        /// </summary>
        public void InvalidateUser(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            try { _db.DeleteRaCacheByOwner(OwnerForUser(username)); }
            catch (Exception ex) { Trace.WriteLine($"[RA] cache wipe failed for {username}: {ex.Message}"); }
        }

        // ── Convenience accessors (panels add more in their phases) ────────

        /// <summary>Profile header data (#29). Cached 1h per user.</summary>
        public Task<RAUserProfile?> GetProfileAsync(CancellationToken ct = default)
        {
            var user = CurrentUser();
            if (user == null) return Task.FromResult<RAUserProfile?>(null);
            return GetCachedAsync<RAUserProfile>(
                $"user_profile:user={user}",
                OwnerForUser(user),
                TtlProfile,
                inner => _api.GetUserProfileAsync(user, inner),
                ct);
        }

        /// <summary>Total points + softcore points (#28). Cached 1h per user.</summary>
        public Task<RAUserPoints?> GetPointsAsync(CancellationToken ct = default)
        {
            var user = CurrentUser();
            if (user == null) return Task.FromResult<RAUserPoints?>(null);
            return GetCachedAsync<RAUserPoints>(
                $"user_points:user={user}",
                OwnerForUser(user),
                TtlPoints,
                inner => _api.GetUserPointsAsync(user, inner),
                ct);
        }

        /// <summary>
        /// Recent unlock feed (#31). 7-day window keeps the feed populated
        /// even for users who only play a few sessions a week; we cap render
        /// at 20 on the UI side. Cached 5 min per user.
        /// </summary>
        public Task<List<RAUserRecentAchievement>?> GetRecentAsync(CancellationToken ct = default)
        {
            var user = CurrentUser();
            if (user == null) return Task.FromResult<List<RAUserRecentAchievement>?>(null);
            return GetCachedAsync<List<RAUserRecentAchievement>>(
                $"user_recent:user={user}",
                OwnerForUser(user),
                TtlRecentActivity,
                async inner =>
                {
                    var list = await _api.GetUserRecentAchievementsAsync(user, 60 * 24 * 7, inner).ConfigureAwait(false);
                    // GetUserRecentAchievementsAsync never returns null, so the
                    // empty-list-instead-of-null avoids confusing the cache
                    // wrapper's "null = network failure" signal.
                    return list;
                },
                ct);
        }

        private static T? Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<T>(json, _jsonOpts); }
            catch { return null; }
        }
    }
}
