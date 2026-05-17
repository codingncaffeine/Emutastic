using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emutastic.Configuration;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Two roles, deliberately kept in one class:
    ///
    /// 1. **Credential validation** via rcheevos (TestLoginAsync — original).
    /// 2. **Public Web API** access for game-wide and per-user stats — used by
    ///    the detail card to show time-to-beat / time-to-master / achievement
    ///    counts / "coming up" without paying rcheevos's identify-and-load cost.
    ///
    /// rcheevos and the Web API are completely separate surfaces. The rcheevos
    /// session token (filled by <see cref="TestLoginAsync"/>) authenticates
    /// achievement unlocks; the Web API needs a different secret — the user's
    /// Web API Key from retroachievements.org/controlpanel.php — set in
    /// Preferences → RetroAchievements and persisted as
    /// <c>RetroAchievementsConfiguration.ApiKey</c>.
    /// </summary>
    public class RetroAchievementsService
    {
        // Single shared HttpClient per .NET guidance — never disposed for the
        // lifetime of the app. 15s is generous; the API is normally <500ms
        // but the host occasionally degrades during nightly DB regenerations.
        // User-Agent identifies us to RA — same string the rcheevos bridge
        // sends, so the server sees a consistent client across both surfaces.
        private static readonly HttpClient _http = BuildHttp();

        private static HttpClient BuildHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(EmutasticUserAgent.Build());
            return http;
        }

        // Cap concurrent Web API calls at 2 to stay polite — the API host is
        // a community service, not a CDN. Library-wide batch refreshes can
        // queue behind a detail-card open without causing a flood.
        private static readonly SemaphoreSlim _throttle = new(2, 2);

        private const string ApiBase = "https://retroachievements.org/API";

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly IConfigurationService? _config;
        private readonly DatabaseService? _db;

        public RetroAchievementsService() { }

        public RetroAchievementsService(IConfigurationService config)
        {
            _config = config;
        }

        public RetroAchievementsService(IConfigurationService config, DatabaseService db)
        {
            _config = config;
            _db = db;
        }

        // TTLs for the two cached responses. Progression rarely changes (the
        // medians shift slowly as more players touch the set); per-user state
        // changes every play session.
        public static readonly TimeSpan ProgressionTtl   = TimeSpan.FromHours(24);
        public static readonly TimeSpan UserProgressTtl  = TimeSpan.FromHours(1);

        // ── 3) Orchestration — refresh + persist for the detail card ────────

        /// <summary>
        /// Refreshes the cached Web API state for a single game and persists
        /// the responses to the DB. Skips network calls for fresh cache, no
        /// API key, or games without an RA game ID. Updates the Game object
        /// in-place so the caller can read the typed views without reloading
        /// from the DB. Never throws.
        /// </summary>
        public async Task RefreshDetailForGameAsync(Game game, CancellationToken ct = default)
        {
            if (game == null || _db == null || game.RAGameId <= 0) return;
            var ra = _config?.GetRetroAchievementsConfiguration();
            if (ra == null || string.IsNullOrWhiteSpace(ra.ApiKey)) return;

            // Game-wide progression (no user needed).
            if (game.IsRAProgressionStale(ProgressionTtl))
            {
                var prog = await GetGameProgressionAsync(game.RAGameId, ct).ConfigureAwait(false);
                if (prog != null)
                {
                    string json = JsonSerializer.Serialize(prog);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    try { _db.UpdateRAProgression(game.Id, json, ts); }
                    catch (Exception ex) { Trace.WriteLine($"[RA] persist progression failed: {ex.Message}"); }
                    game.RAProgressionJson = json;
                    game.RAProgressionFetchedAt = ts;
                }
            }

            // Per-user (only if logged in).
            if (!string.IsNullOrWhiteSpace(ra.Username) && game.IsRAUserProgressStale(UserProgressTtl))
            {
                var user = await GetGameInfoAndUserProgressAsync(game.RAGameId, ra.Username, ct)
                    .ConfigureAwait(false);
                if (user != null)
                {
                    string json = JsonSerializer.Serialize(user);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    try { _db.UpdateRAUserProgress(game.Id, json, ts); }
                    catch (Exception ex) { Trace.WriteLine($"[RA] persist user progress failed: {ex.Message}"); }
                    game.RAUserProgressJson = json;
                    game.RAUserProgressFetchedAt = ts;
                }
            }
        }

        /// <summary>
        /// Marks the per-user cache stale so the next detail-card open
        /// refetches. Cheap — DB-only, no network. Call after the user exits
        /// a game so their freshly-unlocked achievements show up next time
        /// they peek at the card.
        /// </summary>
        public void InvalidateUserProgressForGame(Game game)
        {
            if (game == null || _db == null || game.RAGameId <= 0) return;
            try { _db.UpdateRAUserProgress(game.Id, "", 0L); }
            catch (Exception ex) { Trace.WriteLine($"[RA] invalidate failed: {ex.Message}"); }
            game.RAUserProgressJson = "";
            game.RAUserProgressFetchedAt = 0L;
        }

        // ── 1) Credential validation via rcheevos ───────────────────────────

        /// <summary>
        /// Validates credentials by attempting a password login via rcheevos.
        /// Returns null on success, or an error message on failure.
        /// </summary>
        public Task<(string? error, string? token)> TestLoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                return Task.FromResult<(string?, string?)>(("Username is required.", null));
            if (string.IsNullOrWhiteSpace(password))
                return Task.FromResult<(string?, string?)>(("Password is required.", null));

            return Task.Run<(string?, string?)>(() =>
            {
                RetroAchievementsClient? client = null;
                try
                {
                    client = new RetroAchievementsClient();
                    client.Initialize(null!, false);
                    var (ok, err, token) = client.LoginWithPassword(username, password);
                    return ok ? (null, token) : (err ?? "Login failed.", null);
                }
                catch (Exception ex)
                {
                    return ($"Error: {ex.Message}", null);
                }
                finally
                {
                    try { client?.Dispose(); } catch { }
                }
            });
        }

        // ── 2) Web API ──────────────────────────────────────────────────────

        /// <summary>
        /// Fetches game-wide progression stats — community medians for time
        /// to beat / complete / master, plus per-achievement metadata. No user
        /// context needed. Returns null on any failure (missing API key,
        /// network error, parse failure); never throws.
        /// </summary>
        public Task<RAProgression?> GetGameProgressionAsync(int raGameId, CancellationToken ct = default)
        {
            if (raGameId <= 0) return Task.FromResult<RAProgression?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAProgression?>(null);

            string url = $"{ApiBase}/API_GetGameProgression.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&i={raGameId}";
            return GetJsonAsync<RAProgression>(url, "GetGameProgression", ct);
        }

        /// <summary>
        /// Fetches the given user's per-achievement unlock state for a single
        /// game (DateEarned populated only for earned achievements). Returns
        /// null on any failure.
        /// </summary>
        public Task<RAUserProgress?> GetGameInfoAndUserProgressAsync(
            int raGameId, string username, CancellationToken ct = default)
        {
            if (raGameId <= 0 || string.IsNullOrWhiteSpace(username))
                return Task.FromResult<RAUserProgress?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAUserProgress?>(null);

            string url = $"{ApiBase}/API_GetGameInfoAndUserProgress.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&u={Uri.EscapeDataString(username)}"
                       + $"&g={raGameId}";
            return GetJsonAsync<RAUserProgress>(url, "GetGameInfoAndUserProgress", ct);
        }

        /// <summary>
        /// Batch endpoint: per-game NumAchieved / score totals for the user
        /// across many games in one call. Right tool for refreshing the
        /// library's tile-level state cheaply. Returned dictionary is keyed
        /// by RA game ID. Returns null on failure.
        ///
        /// Currently UNUSED by Phase 1 (detail card consumes the full
        /// GetGameInfoAndUserProgress shape per-game). Kept for the planned
        /// tile-level achievement badges that read these totals.
        /// </summary>
        public async Task<Dictionary<int, RABatchUserProgress>?> GetUserProgressBatchAsync(
            string username, IEnumerable<int> raGameIds, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username) || raGameIds == null) return null;
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return null;

            var ids = raGameIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, RABatchUserProgress>();

            // No documented max; 100 per call is comfortably safe.
            const int BatchSize = 100;
            var merged = new Dictionary<int, RABatchUserProgress>(ids.Count);
            for (int i = 0; i < ids.Count; i += BatchSize)
            {
                var chunk = ids.GetRange(i, Math.Min(BatchSize, ids.Count - i));
                string idCsv = string.Join(",", chunk);
                string url = $"{ApiBase}/API_GetUserProgress.php"
                           + $"?y={Uri.EscapeDataString(key)}"
                           + $"&u={Uri.EscapeDataString(username)}"
                           + $"&i={idCsv}";

                var raw = await GetJsonAsync<Dictionary<string, RABatchUserProgress>>(
                    url, "GetUserProgress", ct).ConfigureAwait(false);
                if (raw == null) continue;

                foreach (var kvp in raw)
                    if (int.TryParse(kvp.Key, out int id))
                        merged[id] = kvp.Value;
            }
            return merged;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Achievements-tab endpoints (v1)
        // 18 read-only Web API methods feeding the dedicated tab. All share
        // the existing _throttle (max 2 concurrent) and _http instance —
        // adding new methods to this class instead of spinning a parallel
        // pool means total RA-host concurrency stays at 2, per polite-citizen
        // convention. Every method returns null on any failure (missing
        // API key, network error, parse failure); never throws.
        //
        // Paginated endpoints (GetUserCompletionProgress, GetGameList,
        // GetRecentGameAwards, GetUserWantToPlayList) handle paging
        // internally — callers receive the consolidated list. 500/page is
        // RA's documented max.
        // ═══════════════════════════════════════════════════════════════════

        // ── #29 GetUserProfile ─────────────────────────────────────────────
        public Task<RAUserProfile?> GetUserProfileAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return Task.FromResult<RAUserProfile?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAUserProfile?>(null);
            string url = $"{ApiBase}/API_GetUserProfile.php?y={Uri.EscapeDataString(key)}&u={Uri.EscapeDataString(username)}";
            return GetJsonAsync<RAUserProfile>(url, "GetUserProfile", ct);
        }

        // ── #28 GetUserPoints ──────────────────────────────────────────────
        public Task<RAUserPoints?> GetUserPointsAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return Task.FromResult<RAUserPoints?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAUserPoints?>(null);
            string url = $"{ApiBase}/API_GetUserPoints.php?y={Uri.EscapeDataString(key)}&u={Uri.EscapeDataString(username)}";
            return GetJsonAsync<RAUserPoints>(url, "GetUserPoints", ct);
        }

        // ── #31 GetUserRecentAchievements ──────────────────────────────────
        // Returns an array of unlocks earned in the last `minutes` (default
        // 60 per RA convention). Tail the array; newest first.
        public async Task<List<RAUserRecentAchievement>> GetUserRecentAchievementsAsync(
            string username, int minutes = 60, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return new List<RAUserRecentAchievement>();
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return new List<RAUserRecentAchievement>();
            string url = $"{ApiBase}/API_GetUserRecentAchievements.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&u={Uri.EscapeDataString(username)}"
                       + $"&m={minutes}";
            var list = await GetJsonAsync<List<RAUserRecentAchievement>>(
                url, "GetUserRecentAchievements", ct).ConfigureAwait(false);
            return list ?? new List<RAUserRecentAchievement>();
        }

        // ── #22 GetUserAwards ──────────────────────────────────────────────
        public Task<RAUserAwards?> GetUserAwardsAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return Task.FromResult<RAUserAwards?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAUserAwards?>(null);
            string url = $"{ApiBase}/API_GetUserAwards.php?y={Uri.EscapeDataString(key)}&u={Uri.EscapeDataString(username)}";
            return GetJsonAsync<RAUserAwards>(url, "GetUserAwards", ct);
        }

        // ── #25 GetUserCompletionProgress (paginated 500/page) ─────────────
        // Pages internally and concatenates. Callers receive the full list.
        // Worst case for a 5000-game RA history: 10 HTTP calls @ ~500ms each
        // behind the throttle — done on a background task, never blocks UI.
        public async Task<List<RAUserCompletionProgressItem>> GetUserCompletionProgressAsync(
            string username, CancellationToken ct = default)
        {
            var all = new List<RAUserCompletionProgressItem>();
            if (string.IsNullOrWhiteSpace(username)) return all;
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return all;

            const int PageSize = 500;
            int offset = 0;
            while (true)
            {
                string url = $"{ApiBase}/API_GetUserCompletionProgress.php"
                           + $"?y={Uri.EscapeDataString(key)}"
                           + $"&u={Uri.EscapeDataString(username)}"
                           + $"&c={PageSize}&o={offset}";
                var page = await GetJsonAsync<RAUserCompletionProgressResponse>(
                    url, "GetUserCompletionProgress", ct).ConfigureAwait(false);
                if (page == null || page.Results == null || page.Results.Count == 0) break;
                all.AddRange(page.Results);
                if (all.Count >= page.Total || page.Results.Count < PageSize) break;
                offset += PageSize;
            }
            return all;
        }

        // ── #32 GetUserRecentlyPlayedGames ─────────────────────────────────
        public async Task<List<RARecentlyPlayedGame>> GetUserRecentlyPlayedGamesAsync(
            string username, int count = 50, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return new List<RARecentlyPlayedGame>();
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return new List<RARecentlyPlayedGame>();
            string url = $"{ApiBase}/API_GetUserRecentlyPlayedGames.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&u={Uri.EscapeDataString(username)}"
                       + $"&c={count}";
            var list = await GetJsonAsync<List<RARecentlyPlayedGame>>(
                url, "GetUserRecentlyPlayedGames", ct).ConfigureAwait(false);
            return list ?? new List<RARecentlyPlayedGame>();
        }

        // ── #35 GetUserWantToPlayList (paginated, mutual-follow only) ──────
        public async Task<List<RAWantToPlayItem>> GetUserWantToPlayListAsync(
            string username, CancellationToken ct = default)
        {
            var all = new List<RAWantToPlayItem>();
            if (string.IsNullOrWhiteSpace(username)) return all;
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return all;

            const int PageSize = 500;
            int offset = 0;
            while (true)
            {
                string url = $"{ApiBase}/API_GetUserWantToPlayList.php"
                           + $"?y={Uri.EscapeDataString(key)}"
                           + $"&u={Uri.EscapeDataString(username)}"
                           + $"&c={PageSize}&o={offset}";
                var page = await GetJsonAsync<RAWantToPlayResponse>(
                    url, "GetUserWantToPlayList", ct).ConfigureAwait(false);
                if (page == null || page.Results == null || page.Results.Count == 0) break;
                all.AddRange(page.Results);
                if (all.Count >= page.Total || page.Results.Count < PageSize) break;
                offset += PageSize;
            }
            return all;
        }

        // ── #3 GetAchievementOfTheWeek ─────────────────────────────────────
        public Task<RAAchievementOfTheWeek?> GetAchievementOfTheWeekAsync(CancellationToken ct = default)
        {
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAAchievementOfTheWeek?>(null);
            string url = $"{ApiBase}/API_GetAchievementOfTheWeek.php?y={Uri.EscapeDataString(key)}";
            return GetJsonAsync<RAAchievementOfTheWeek>(url, "GetAchievementOfTheWeek", ct);
        }

        // ── #20 GetRecentGameAwards (paginated) ────────────────────────────
        public async Task<List<RARecentGameAward>> GetRecentGameAwardsAsync(
            DateTimeOffset? startingFrom = null, int count = 25, CancellationToken ct = default)
        {
            var all = new List<RARecentGameAward>();
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return all;

            string url = $"{ApiBase}/API_GetRecentGameAwards.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&c={count}";
            if (startingFrom.HasValue)
                url += $"&d={Uri.EscapeDataString(startingFrom.Value.ToString("yyyy-MM-dd"))}";

            var page = await GetJsonAsync<RARecentGameAwardsResponse>(
                url, "GetRecentGameAwards", ct).ConfigureAwait(false);
            if (page?.Results != null) all.AddRange(page.Results);
            return all;
        }

        // ── #21 GetTopTenUsers ─────────────────────────────────────────────
        // The API returns a JSON array of OBJECTS keyed by numeric strings:
        //   [{"1": "MaxMilyin", "2": 399597, "3": 1599212, "4": "<ULID>"}, ...]
        //   1 = username, 2 = hardcore points, 3 = retro/white points, 4 = ULID
        // The official api-js client normalizes this to readable fields, but
        // the raw HTTP response is positional-by-string-key. We project into
        // a typed tuple here so the cache layer can JSON-serialize a clean
        // shape downstream.
        public async Task<List<(string User, int Points, int RetroPoints)>> GetTopTenUsersAsync(CancellationToken ct = default)
        {
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return new();
            string url = $"{ApiBase}/API_GetTopTenUsers.php?y={Uri.EscapeDataString(key)}";

            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Trace.WriteLine($"[RA] GetTopTenUsers HTTP {(int)resp.StatusCode}");
                    return new();
                }
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var doc = JsonDocument.Parse(json);
                var result = new List<(string, int, int)>();
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in doc.RootElement.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        string user = "";
                        int pts = 0;
                        int rpts = 0;
                        if (row.TryGetProperty("1", out var userProp))
                            user = userProp.GetString() ?? "";
                        if (row.TryGetProperty("2", out var ptsProp))
                            pts = ptsProp.ValueKind == JsonValueKind.Number && ptsProp.TryGetInt32(out int p) ? p : 0;
                        if (row.TryGetProperty("3", out var rptsProp))
                            rpts = rptsProp.ValueKind == JsonValueKind.Number && rptsProp.TryGetInt32(out int rp) ? rp : 0;
                        if (!string.IsNullOrEmpty(user))
                            result.Add((user, pts, rpts));
                    }
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RA] GetTopTenUsers failed: {ex.Message}");
                return new();
            }
            finally { try { _throttle.Release(); } catch { } }
        }

        // ── #5 GetAchievementsEarnedBetween ────────────────────────────────
        // Used by the heatmap to bulk-fetch a date range, then derived into
        // {date → count} aggregates persisted to ra_heatmap_daily. Returns
        // null on network failure (so the heatmap caller can distinguish
        // "fetched, no unlocks" from "couldn't fetch" and avoid persisting
        // poisoned zeros for past days).
        public Task<List<RAEarnedAchievement>?> GetAchievementsEarnedBetweenAsync(
            string username, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return Task.FromResult<List<RAEarnedAchievement>?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<List<RAEarnedAchievement>?>(null);
            string url = $"{ApiBase}/API_GetAchievementsEarnedBetween.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&u={Uri.EscapeDataString(username)}"
                       + $"&f={fromUtc.ToUnixTimeSeconds()}"
                       + $"&t={toUtc.ToUnixTimeSeconds()}";
            return GetJsonAsync<List<RAEarnedAchievement>>(url, "GetAchievementsEarnedBetween", ct);
        }

        // ── #6 GetAchievementsEarnedOnDay ──────────────────────────────────
        // Drill-in for a heatmap cell click. Returns the full per-achievement
        // payload for that single day — not cached, fetched on click.
        public async Task<List<RAEarnedAchievement>> GetAchievementsEarnedOnDayAsync(
            string username, DateTime day, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return new();
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return new();
            string url = $"{ApiBase}/API_GetAchievementsEarnedOnDay.php"
                       + $"?y={Uri.EscapeDataString(key)}"
                       + $"&u={Uri.EscapeDataString(username)}"
                       + $"&d={Uri.EscapeDataString(day.ToString("yyyy-MM-dd"))}";
            var list = await GetJsonAsync<List<RAEarnedAchievement>>(
                url, "GetAchievementsEarnedOnDay", ct).ConfigureAwait(false);
            return list ?? new();
        }

        // ── #1 GetAchievementCount ─────────────────────────────────────────
        public Task<RAAchievementCount?> GetAchievementCountAsync(int raGameId, CancellationToken ct = default)
        {
            if (raGameId <= 0) return Task.FromResult<RAAchievementCount?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAAchievementCount?>(null);
            string url = $"{ApiBase}/API_GetAchievementCount.php?y={Uri.EscapeDataString(key)}&i={raGameId}";
            return GetJsonAsync<RAAchievementCount>(url, "GetAchievementCount", ct);
        }

        // ── #2 GetAchievementDistribution ──────────────────────────────────
        // Returns a small JSON object { "1": 12000, "2": 8000, ... } so we
        // round-trip through Dictionary<string,int> directly.
        public async Task<RAAchievementDistribution?> GetAchievementDistributionAsync(int raGameId, CancellationToken ct = default)
        {
            if (raGameId <= 0) return null;
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return null;
            string url = $"{ApiBase}/API_GetAchievementDistribution.php?y={Uri.EscapeDataString(key)}&i={raGameId}";
            var buckets = await GetJsonAsync<Dictionary<string, int>>(
                url, "GetAchievementDistribution", ct).ConfigureAwait(false);
            return buckets == null ? null : new RAAchievementDistribution { Buckets = buckets };
        }

        // ── #10 GetConsoleIDs ──────────────────────────────────────────────
        public async Task<List<RAConsole>> GetConsoleIdsAsync(CancellationToken ct = default)
        {
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return new();
            string url = $"{ApiBase}/API_GetConsoleIDs.php?y={Uri.EscapeDataString(key)}&a=1&g=1";
            var list = await GetJsonAsync<List<RAConsole>>(url, "GetConsoleIDs", ct).ConfigureAwait(false);
            return list ?? new();
        }

        // ── #12 GetGameHashes ──────────────────────────────────────────────
        public Task<RAGameHashesResponse?> GetGameHashesAsync(int raGameId, CancellationToken ct = default)
        {
            if (raGameId <= 0) return Task.FromResult<RAGameHashesResponse?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAGameHashesResponse?>(null);
            string url = $"{ApiBase}/API_GetGameHashes.php?y={Uri.EscapeDataString(key)}&i={raGameId}";
            return GetJsonAsync<RAGameHashesResponse>(url, "GetGameHashes", ct);
        }

        // ── #15 GetGameList (paginated, per console; HEAVY) ─────────────────
        // Whole-console game dumps. Docs explicitly warn "aggressively cache."
        // Big consoles (NES has 1500+ games) exceed RA's 500/page default, so
        // page until the response returns < 500. Cached at 7-day TTL so the
        // multi-page wait happens at most weekly.
        public async Task<List<RAGameListItem>> GetGameListAsync(
            int consoleId, bool onlyWithAchievements = true, bool includeHashes = false, CancellationToken ct = default)
        {
            var all = new List<RAGameListItem>();
            if (consoleId <= 0) return all;
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return all;

            const int PageSize = 500;
            int offset = 0;
            while (true)
            {
                string url = $"{ApiBase}/API_GetGameList.php"
                           + $"?y={Uri.EscapeDataString(key)}"
                           + $"&i={consoleId}"
                           + $"&f={(onlyWithAchievements ? 1 : 0)}"
                           + $"&h={(includeHashes ? 1 : 0)}"
                           + $"&c={PageSize}&o={offset}";
                var page = await GetJsonAsync<List<RAGameListItem>>(url, "GetGameList", ct).ConfigureAwait(false);
                if (page == null || page.Count == 0) break;
                all.AddRange(page);
                if (page.Count < PageSize) break;
                offset += PageSize;
            }
            return all;
        }

        // ── #18 GetGame ────────────────────────────────────────────────────
        public Task<RAGameMinimal?> GetGameAsync(int raGameId, CancellationToken ct = default)
        {
            if (raGameId <= 0) return Task.FromResult<RAGameMinimal?>(null);
            string? key = GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return Task.FromResult<RAGameMinimal?>(null);
            string url = $"{ApiBase}/API_GetGame.php?y={Uri.EscapeDataString(key)}&i={raGameId}";
            return GetJsonAsync<RAGameMinimal>(url, "GetGame", ct);
        }

        private async Task<T?> GetJsonAsync<T>(string url, string opName, CancellationToken ct)
            where T : class
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var resp = await _http.GetAsync(url,
                    HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string body = "";
                    try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                    if (body.Length > 240) body = body.Substring(0, 240);
                    Trace.WriteLine($"[RA] {opName} HTTP {(int)resp.StatusCode}");
                    RaLog.Write($"http error: op={opName} status={(int)resp.StatusCode} body={body}");
                    return null;
                }
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                try { return JsonSerializer.Deserialize<T>(json, _jsonOpts); }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RA] {opName} JSON parse failed: {ex.Message}");
                    string snippet = json ?? "";
                    if (snippet.Length > 240) snippet = snippet.Substring(0, 240);
                    RaLog.Write($"parse failed: op={opName} err={ex.Message} json={snippet}");
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RA] {opName} failed: {ex.GetType().Name}: {ex.Message}");
                RaLog.Write($"exception: op={opName} type={ex.GetType().Name} msg={ex.Message}");
                return null;
            }
            finally
            {
                try { _throttle.Release(); } catch { }
            }
        }

        /// <summary>
        /// Web API key lookup. Prefers the explicitly-injected config; falls
        /// back to App.Configuration for the parameterless-constructor path
        /// kept for backward compat with PreferencesWindow's TestLoginAsync use.
        /// </summary>
        private string? GetApiKey()
        {
            var ra = _config?.GetRetroAchievementsConfiguration()
                  ?? App.Configuration?.GetRetroAchievementsConfiguration();
            return ra?.ApiKey;
        }
    }

    /// <summary>
    /// Response item for GetUserProgress batch endpoint. Distinct from
    /// RAUserProgress (the full per-game shape) because the batch endpoint
    /// returns only totals — no per-achievement detail.
    /// </summary>
    public sealed class RABatchUserProgress
    {
        [JsonPropertyName("NumPossibleAchievements")] public int NumPossibleAchievements { get; set; }
        [JsonPropertyName("PossibleScore")]           public int PossibleScore { get; set; }
        [JsonPropertyName("NumAchieved")]             public int NumAchieved { get; set; }
        [JsonPropertyName("ScoreAchieved")]           public int ScoreAchieved { get; set; }
        [JsonPropertyName("NumAchievedHardcore")]     public int NumAchievedHardcore { get; set; }
        [JsonPropertyName("ScoreAchievedHardcore")]   public int ScoreAchievedHardcore { get; set; }
    }
}
