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
        // 15 min on profile + points so things like avatar changes /
        // username edits / rank shifts surface within a tab-revisit cycle
        // instead of waiting up to an hour. Cheap endpoints; fine to refetch.
        public static readonly TimeSpan TtlProfile            = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan TtlPoints             = TimeSpan.FromMinutes(15);
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
        /// Returns the unix-seconds fetched_at stamp for the given cache key,
        /// or 0 if the row doesn't exist. Used by image-loading code to bust
        /// the WPF BitmapImage cache when the data behind a stable URL has
        /// changed (e.g. RA serves a user's new avatar at the same UserPic
        /// path — same URL, new bytes; WPF caches by URL so it won't see the
        /// new bytes without an explicit cache-buster query string).
        /// </summary>
        public long PeekCachedFetchedAt(string cacheKey)
        {
            try { return _db.GetRaCache(cacheKey)?.FetchedAt ?? 0L; }
            catch { return 0L; }
        }

        /// <summary>
        /// One-shot variant that returns both the deserialized payload and
        /// the fetched_at stamp from a single DB lookup. Used on hot paths
        /// where the caller needs both (e.g. cold-paint profile render
        /// + avatar cache-buster) to avoid two consecutive PK reads.
        /// </summary>
        public (T? Payload, long FetchedAt) PeekCachedWithMeta<T>(string cacheKey) where T : class
        {
            try
            {
                var row = _db.GetRaCache(cacheKey);
                if (row == null) return (null, 0L);
                T? typed = string.IsNullOrEmpty(row.Payload) ? null : Deserialize<T>(row.Payload);
                return (typed, row.FetchedAt);
            }
            catch { return (null, 0L); }
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

        /// <summary>
        /// Marks every cached user-keyed payload stale on app start so the
        /// first Achievements tab visit refetches profile / points / awards
        /// / recent / spotlight / heatmap-today without honoring the
        /// in-session TTL across an app-restart boundary.
        ///
        /// Payloads stay in the row for cold-paint fallback (PeekCached).
        /// Global rows (Achievement-of-the-Week, recent-game-awards,
        /// top-ten) keep their stamps because TTL behavior across restarts
        /// is exactly what we want for them.
        ///
        /// Idempotent and safe to call on every MainWindow load — does
        /// nothing if no user is configured.
        /// </summary>
        public void MarkUserCacheStaleForFreshFetch()
        {
            var user = CurrentUser();
            if (string.IsNullOrWhiteSpace(user)) return;
            try
            {
                _db.MarkRaCacheStaleByOwner(OwnerForUser(user));
            }
            catch (Exception ex)
            {
                // Cache invalidation is best-effort. A failure here means the
                // user sees up-to-15-min-old cached profile/points until the
                // TTL expires naturally — not a correctness bug, just a
                // latency one. Keep the trace so we can spot it if it ever
                // happens in the wild.
                Trace.WriteLine($"[RA] session-start cache invalidate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Drops the cached Library Spotlight materialization + the recent-
        /// unlocks feed. Called from the post-emulator-exit hook so when the
        /// user finishes a play session and pops back to the Achievements
        /// tab, they see a fresh "Closest to mastering" / "Quick wins" / feed
        /// instead of last-session stale data.
        /// </summary>
        public void InvalidatePostPlay()
        {
            var user = CurrentUser();
            if (user == null) return;
            try
            {
                // Set fetched_at=0 on the rows so the next GetCachedAsync
                // forces a refetch but keeps the row as a fallback if the
                // network fails.
                long zero = 0L;
                _db.SetRaCache($"library_spotlight:v2:user={user}", OwnerForUser(user),
                    _db.GetRaCache($"library_spotlight:v2:user={user}")?.Payload ?? "", zero, 0);
                _db.SetRaCache($"user_recent:v2:user={user}", OwnerForUser(user),
                    _db.GetRaCache($"user_recent:v2:user={user}")?.Payload ?? "", zero, 0);
            }
            catch (Exception ex) { Trace.WriteLine($"[RA] post-play invalidate failed: {ex.Message}"); }
        }

        // ── Convenience accessors (panels add more in their phases) ────────

        /// <summary>Profile header data (#29). Cached 15 min per user.</summary>
        public Task<RAUserProfile?> GetProfileAsync(CancellationToken ct = default)
        {
            var user = CurrentUser();
            if (user == null) return Task.FromResult<RAUserProfile?>(null);
            return GetCachedAsync<RAUserProfile>(
                $"user_profile:v2:user={user}",
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
                $"user_points:v2:user={user}",
                OwnerForUser(user),
                TtlPoints,
                inner => _api.GetUserPointsAsync(user, inner),
                ct);
        }

        /// <summary>
        /// Materialized Library Spotlight — the five cross-reference panels
        /// (Closest to mastering / Quick wins / Continue / Never started /
        /// Wishlist owned). Combines RA's per-user completion stream with
        /// the local library's RA-tagged games. Runs entirely on a background
        /// thread; computation is in-memory hash-set intersections, no
        /// per-render DB joins. Cached 15 min so UI binds to a pre-built
        /// snapshot.
        /// </summary>
        public async Task<RALibrarySpotlight?> GetLibrarySpotlightAsync(CancellationToken ct = default)
        {
            var user = CurrentUser();
            if (user == null || !HasApiKey()) return null;

            // :v2 — relaxed the local-library filter on closest-to-mastering
            // and continue-where-left-off, raised tile cap to 10. Bumping the
            // key forces a fresh materialization on next visit rather than
            // serving the prior strict-filter snapshot for up to 15 min.
            string cacheKey = $"library_spotlight:v2:user={user}";
            // Peek the prior snapshot (if any) — used as last-good fallback
            // when one of the parallel RA fetches below comes back empty
            // (typically because of a 429 race). Without this, a transient
            // error wipes the panel until the next 15-min TTL refresh lands.
            var priorSnapshot = PeekCached<RALibrarySpotlight>(cacheKey);
            return await GetCachedAsync<RALibrarySpotlight>(
                cacheKey,
                OwnerForUser(user),
                TtlCompletionProgress,
                async inner =>
                {
                    // 1. Owned RA game IDs from the local library — single
                    //    DB scan, hash-set intersection downstream.
                    var ownedRaIds = _db.GetOwnedRAGameIds();
                    if (ownedRaIds.Count == 0) return new RALibrarySpotlight();

                    // 2. Load Game records once so the panels can render
                    //    titles/consoles + know how to launch.
                    var ownedGames = _db.GetAllGames()
                        .Where(g => g.RAGameId > 0 && ownedRaIds.Contains(g.RAGameId))
                        .GroupBy(g => g.RAGameId)
                        .ToDictionary(g => g.Key, g => g.First());

                    // 3. Parallel-fan the three user-scoped fetches so we
                    //    don't pay the throttle wait three times in series.
                    var completionTask    = _api.GetUserCompletionProgressAsync(user, inner);
                    var recentlyPlayedTask = _api.GetUserRecentlyPlayedGamesAsync(user, 50, inner);
                    var wishlistTask       = _api.GetUserWantToPlayListAsync(user, inner);
                    await Task.WhenAll(completionTask, recentlyPlayedTask, wishlistTask).ConfigureAwait(false);

                    var completion    = completionTask.Result    ?? new();
                    var recentlyPlayed = recentlyPlayedTask.Result ?? new();
                    var wishlist       = wishlistTask.Result       ?? new();

                    var spotlight = new RALibrarySpotlight();

                    // ── Closest to mastering ─────────────────────────────
                    // Every game the user has started but not finished, no
                    // local-library filter — the value here is seeing the
                    // user's RA progress, regardless of whether they've
                    // launched that game in Emutastic yet. Sort: remaining
                    // achievements ascending (smallest gap leftmost), tied
                    // by completion ratio descending. Cap at 10.
                    spotlight.ClosestToMastering = completion
                        .Where(p => p.NumAwarded > 0 && p.NumAwarded < p.MaxPossible)
                        .OrderBy(p => p.MaxPossible - p.NumAwarded)
                        .ThenByDescending(p => (double)p.NumAwarded / p.MaxPossible)
                        .Take(10)
                        .Select(p => new RASpotlightGame
                        {
                            RAGameId = p.GameId,
                            LocalGameId = ownedGames.TryGetValue(p.GameId, out var g) ? g.Id : 0,
                            Title = p.Title,
                            Console = p.ConsoleName,
                            ImageIcon = p.ImageIcon,
                            NumAchieved = p.NumAwarded,
                            MaxPossible = p.MaxPossible,
                            Subtitle = $"{p.NumAwarded}/{p.MaxPossible} · {p.MaxPossible - p.NumAwarded} to go",
                        })
                        .ToList();

                    // ── Continue where you left off ───────────────────────
                    // RA's most-recently-played list as-is (no local filter).
                    // Most-recent first; cap at 10.
                    spotlight.ContinueWhereLeftOff = recentlyPlayed
                        .Take(10)
                        .Select(r => new RASpotlightGame
                        {
                            RAGameId = r.GameId,
                            LocalGameId = ownedGames.TryGetValue(r.GameId, out var g) ? g.Id : 0,
                            Title = r.Title,
                            Console = r.ConsoleName,
                            ImageIcon = r.ImageIcon,
                            NumAchieved = r.NumAchieved,
                            MaxPossible = r.NumPossibleAchievements,
                            Subtitle = r.NumPossibleAchievements > 0
                                ? $"{r.NumAchieved}/{r.NumPossibleAchievements}"
                                : "Played",
                        })
                        .ToList();

                    // ── Never started ─────────────────────────────────────
                    // Owned games with RAGameId that don't show up in the
                    // completion stream (= user has zero unlocks). RA's
                    // image-icon isn't in the completion response, so this
                    // panel uses local cover art instead — looks dramatically
                    // better than a row of empty BgTertiary squares.
                    var touchedIds = new HashSet<int>(completion.Select(p => p.GameId));
                    spotlight.NeverStarted = ownedGames.Values
                        .Where(g => !touchedIds.Contains(g.RAGameId))
                        .Take(10)
                        .Select(g => new RASpotlightGame
                        {
                            RAGameId = g.RAGameId,
                            LocalGameId = g.Id,
                            Title = g.Title,
                            Console = g.Console,
                            ImageIcon = null,
                            LocalArtPath = g.DisplayArtPath,
                            NumAchieved = 0,
                            MaxPossible = 0,
                            Subtitle = "Untouched",
                        })
                        .ToList();

                    // ── Wishlist you own ─────────────────────────────────
                    spotlight.WishlistOwned = wishlist
                        .Where(w => ownedRaIds.Contains(w.Id))
                        .Take(10)
                        .Select(w => new RASpotlightGame
                        {
                            RAGameId = w.Id,
                            LocalGameId = ownedGames.TryGetValue(w.Id, out var g) ? g.Id : 0,
                            Title = w.Title,
                            Console = w.ConsoleName,
                            ImageIcon = w.ImageIcon,
                            NumAchieved = 0,
                            MaxPossible = w.AchievementsPublished,
                            Subtitle = w.AchievementsPublished > 0
                                ? $"{w.AchievementsPublished} achievements · {w.PointsTotal} pts"
                                : "On your wishlist",
                        })
                        .ToList();

                    // ── Quick wins across library ─────────────────────────
                    // Aggregates unearned-with-low-median-TTU candidates
                    // from every owned game that has cached RAProgressionJson
                    // (set by the detail card workflow). No extra API calls.
                    var quickWins = new List<RASpotlightQuickWin>();
                    foreach (var g in ownedGames.Values)
                    {
                        var prog = g.RAProgressionTyped;
                        if (prog == null || prog.Achievements == null || prog.Achievements.Count == 0) continue;
                        var earned = new HashSet<int>();
                        var userProg = g.RAUserProgressTyped;
                        if (userProg != null && userProg.Achievements != null)
                        {
                            foreach (var kv in userProg.Achievements)
                                if (!string.IsNullOrEmpty(kv.Value.DateEarned)) earned.Add(kv.Value.Id);
                        }
                        foreach (var a in prog.Achievements)
                        {
                            if (earned.Contains(a.Id)) continue;
                            if (!a.MedianTimeToUnlock.HasValue || a.MedianTimeToUnlock.Value <= 0) continue;
                            quickWins.Add(new RASpotlightQuickWin
                            {
                                RAGameId = g.RAGameId,
                                LocalGameId = g.Id,
                                GameTitle = g.Title,
                                Console = g.Console,
                                AchievementId = a.Id,
                                AchievementTitle = a.Title,
                                Description = a.Description,
                                BadgeName = a.BadgeName,
                                Points = a.Points,
                                MedianSeconds = a.MedianTimeToUnlock.Value,
                            });
                        }
                    }
                    spotlight.QuickWins = quickWins
                        .OrderBy(q => q.MedianSeconds)
                        .ThenByDescending(q => q.Points)
                        .Take(10)
                        .ToList();

                    // Last-good preservation. If any of the parallel fetches
                    // came back empty but the prior snapshot had data for
                    // that list, prefer the prior. Targets transient HTTP
                    // failures (429s, network blips) — without this the
                    // panel goes blank for up to 15 min until the next
                    // refresh. The genuine-empty case (user actually has
                    // nothing) loses one TTL window of UX correctness;
                    // acceptable for these endpoints (recently-played,
                    // wishlist, completion don't reset to empty in practice).
                    if (priorSnapshot != null)
                    {
                        if (spotlight.ClosestToMastering.Count == 0
                            && priorSnapshot.ClosestToMastering?.Count > 0)
                            spotlight.ClosestToMastering = priorSnapshot.ClosestToMastering;
                        if (spotlight.ContinueWhereLeftOff.Count == 0
                            && priorSnapshot.ContinueWhereLeftOff?.Count > 0)
                            spotlight.ContinueWhereLeftOff = priorSnapshot.ContinueWhereLeftOff;
                        if (spotlight.WishlistOwned.Count == 0
                            && priorSnapshot.WishlistOwned?.Count > 0)
                            spotlight.WishlistOwned = priorSnapshot.WishlistOwned;
                        if (spotlight.QuickWins.Count == 0
                            && priorSnapshot.QuickWins?.Count > 0)
                            spotlight.QuickWins = priorSnapshot.QuickWins;
                    }

                    return spotlight;
                },
                ct).ConfigureAwait(false);
        }

        /// <summary>Achievement of the Week (#3). Cached 24h, global (no per-user owner).</summary>
        public Task<RAAchievementOfTheWeek?> GetAchievementOfTheWeekAsync(CancellationToken ct = default)
        {
            if (!HasApiKey()) return Task.FromResult<RAAchievementOfTheWeek?>(null);
            return GetCachedAsync<RAAchievementOfTheWeek>(
                "achievement_of_the_week",
                "global",
                TtlAchievementOfWeek,
                inner => _api.GetAchievementOfTheWeekAsync(inner),
                ct);
        }

        /// <summary>Recent global mastery awards (#20). Cached 5 min, global.</summary>
        public async Task<List<RARecentGameAward>?> GetRecentGameAwardsAsync(int count = 25, CancellationToken ct = default)
        {
            if (!HasApiKey()) return null;
            return await GetCachedAsync<List<RARecentGameAward>>(
                $"recent_game_awards:c={count}",
                "global",
                TtlRecentGameAwards,
                async inner =>
                {
                    var list = await _api.GetRecentGameAwardsAsync(null, count, inner).ConfigureAwait(false);
                    return list;
                },
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Top 10 site users (#21). Cached 24h, global.
        /// Cache key bumped to :v2 — the v1 implementation expected positional
        /// arrays and silently cached an empty list when the API actually
        /// returns array-of-objects with string keys. Existing v1 rows stay
        /// in ra_cache but go untouched; the cleanup pass at startup or the
        /// existing owner-wipe paths will reclaim them eventually.
        /// </summary>
        public async Task<List<TopTenEntry>?> GetTopTenAsync(CancellationToken ct = default)
        {
            if (!HasApiKey()) return null;
            return await GetCachedAsync<List<TopTenEntry>>(
                "top_ten_users:v2",
                "global",
                TtlTopTen,
                async inner =>
                {
                    var raw = await _api.GetTopTenUsersAsync(inner).ConfigureAwait(false);
                    return raw.Select(t => new TopTenEntry { User = t.User, Points = t.Points, RetroPoints = t.RetroPoints }).ToList();
                },
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Heatmap aggregate for the trailing N days. Reads ra_heatmap_daily
        /// first — past days are written once and never refreshed, so they
        /// short-circuit the API call. Today's row is refetched if older
        /// than TtlHeatmapCurrent (1h) so a play session shows up while the
        /// user is in the tab. Returns a map { "yyyy-MM-dd" → count }.
        ///
        /// Network failure falls back silently to whatever's already in
        /// ra_heatmap_daily for the range.
        /// </summary>
        public async Task<Dictionary<string, int>?> GetHeatmapAsync(int days = 90, CancellationToken ct = default)
        {
            var user = CurrentUser();
            if (user == null || !HasApiKey()) return null;

            var endUtc = DateTime.UtcNow.Date;
            var startUtc = endUtc.AddDays(-(days - 1));
            string startIso = startUtc.ToString("yyyy-MM-dd");
            string endIso = endUtc.ToString("yyyy-MM-dd");
            string todayIso = endIso;

            // 1. Read what's already aggregated. Past days don't need to be
            //    refetched — they're frozen by definition.
            Dictionary<string, int> persisted;
            try { persisted = _db.GetRaHeatmapRange(user, startIso, endIso); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RA] heatmap read failed: {ex.Message}");
                persisted = new();
            }

            // 2. Decide whether we need to refetch today's row. Track via a
            //    cache row in ra_cache (cheaper than parsing a date column
            //    on ra_heatmap_daily). If the today-row was refreshed within
            //    the TTL, skip the API call entirely.
            string todayCacheKey = $"heatmap_today:user={user}:date={todayIso}";
            var todayMarker = _db.GetRaCache(todayCacheKey);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool todayFresh = todayMarker != null
                              && todayMarker.FetchedAt > 0
                              && (now - todayMarker.FetchedAt) < todayMarker.TtlSeconds;

            // 3. Determine which past days are missing from persistence.
            //    Build a set of expected dates and subtract what we have.
            var missing = new List<DateTime>();
            for (var d = startUtc; d <= endUtc; d = d.AddDays(1))
            {
                string iso = d.ToString("yyyy-MM-dd");
                if (iso == todayIso)
                {
                    if (!todayFresh) missing.Add(d);
                }
                else if (!persisted.ContainsKey(iso))
                {
                    missing.Add(d);
                }
            }

            // 4. If everything's cached, return the map immediately.
            if (missing.Count == 0) return persisted;

            // 5. Otherwise fetch the bounding range that covers the missing
            //    span. One network call for the whole range — RA's endpoint
            //    accepts a single from/to and returns every unlock in between.
            DateTime fromUtc = missing.First();
            DateTime toUtc   = missing.Last().AddDays(1).AddSeconds(-1);
            var unlocks = await _api.GetAchievementsEarnedBetweenAsync(
                user,
                new DateTimeOffset(fromUtc, TimeSpan.Zero),
                new DateTimeOffset(toUtc, TimeSpan.Zero),
                ct).ConfigureAwait(false);

            // 6. Network failure → return whatever's already on disk WITHOUT
            //    persisting zeros. Otherwise we'd poison past days as
            //    "fetched and empty" forever (the !ContainsKey check would
            //    pass next launch and skip the retry).
            if (unlocks == null)
            {
                Trace.WriteLine($"[RA] heatmap fetch failed for {fromUtc:yyyy-MM-dd}..{toUtc:yyyy-MM-dd}; serving cached subset");
                return persisted;
            }

            // 7. Bucket unlocks by date, accumulate counts. Missing days are
            //    initialised to 0 so the persist step writes them as
            //    confirmed-empty (which past-day reads short-circuit on
            //    next launch). Today's bucket is initialised to 0 too —
            //    its TTL marker controls when we refetch.
            var freshCounts = new Dictionary<string, int>();
            foreach (var d in missing)
                freshCounts[d.ToString("yyyy-MM-dd")] = 0;

            foreach (var u in unlocks)
            {
                if (string.IsNullOrEmpty(u.Date)) continue;
                // RA returns "yyyy-MM-dd HH:mm:ss" — take the date portion.
                string iso = u.Date.Length >= 10 ? u.Date.Substring(0, 10) : u.Date;
                if (!freshCounts.ContainsKey(iso)) continue;
                freshCounts[iso] = freshCounts[iso] + 1;
            }

            // 8. Persist + merge.
            foreach (var (iso, count) in freshCounts)
            {
                try { _db.SetRaHeatmapDay(user, iso, count); }
                catch (Exception ex) { Trace.WriteLine($"[RA] heatmap persist failed: {ex.Message}"); }
                persisted[iso] = count;
            }

            // 9. Stamp today's refresh marker so we don't fetch again
            //    within the TTL window.
            try { _db.SetRaCache(todayCacheKey, OwnerForUser(user), "1", now, (long)TtlHeatmapCurrent.TotalSeconds); }
            catch { }

            return persisted;
        }

        /// <summary>Trophy case data (#22): mastery / beaten / completion awards. Cached 1h per user.</summary>
        public Task<RAUserAwards?> GetAwardsAsync(CancellationToken ct = default)
        {
            var user = CurrentUser();
            if (user == null) return Task.FromResult<RAUserAwards?>(null);
            return GetCachedAsync<RAUserAwards>(
                $"user_awards:user={user}",
                OwnerForUser(user),
                TtlAwards,
                inner => _api.GetUserAwardsAsync(user, inner),
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
                $"user_recent:v2:user={user}",
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

        // Top-10 row, materialized from the positional-array shape the API
        // returns so we can JSON-serialize for the cache.
        public sealed class TopTenEntry
        {
            public string User { get; set; } = "";
            public int Points { get; set; }
            public int RetroPoints { get; set; }
        }

        private static T? Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<T>(json, _jsonOpts); }
            catch { return null; }
        }
    }
}
