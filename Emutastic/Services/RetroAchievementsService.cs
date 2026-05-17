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
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

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
                    Trace.WriteLine($"[RA] {opName} HTTP {(int)resp.StatusCode}");
                    return null;
                }
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                try { return JsonSerializer.Deserialize<T>(json, _jsonOpts); }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[RA] {opName} JSON parse failed: {ex.Message}");
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
