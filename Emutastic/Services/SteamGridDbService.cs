using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Minimal SteamGridDB API v2 client. Right now it verifies a user's personal API token;
    /// the hi-res art fetch is built on top of this. The token is per-user (Preferences -> EmuTV)
    /// and never embedded in the build. Activity is written to Logs\steamgriddb.log.
    /// </summary>
    public sealed class SteamGridDbService
    {
        private const string BaseUrl = "https://www.steamgriddb.com/api/v2/";
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        internal static void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [SteamGridDB] {msg}";
            System.Diagnostics.Trace.WriteLine(line);
            try
            {
                string logPath = System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "steamgriddb.log");
                LogRotation.RotateIfLarge(logPath);
                System.IO.File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { /* non-fatal */ }
        }

        /// <summary>
        /// Verifies an API token with a lightweight authenticated request.
        /// Returns null on success, or an error string to show the user.
        /// </summary>
        public async Task<string?> TestTokenAsync(string? token)
        {
            token = (token ?? "").Trim();
            if (string.IsNullOrEmpty(token))
            {
                Log("Verify: no token entered.");
                return "Enter your SteamGridDB API token first.";
            }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "search/autocomplete/mario");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.UserAgent.ParseAdd("Emutastic");

                var resp = await _http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                Log($"Verify response ({(int)resp.StatusCode}): {Trunc(body, 300)}");

                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return "Invalid token — check the key at steamgriddb.com (Profile -> Preferences -> API).";
                if (!resp.IsSuccessStatusCode)
                    return $"SteamGridDB returned {(int)resp.StatusCode}.";

                try
                {
                    if ((JsonNode.Parse(body)?["success"]?.GetValue<bool>() ?? true) == false)
                        return "SteamGridDB rejected the token.";
                }
                catch { /* 200 with an unexpected body — still treat as reachable/authorised */ }

                Log("Verify: token OK.");
                return null;
            }
            catch (Exception ex)
            {
                Log($"Verify error: {ex.Message}");
                return "Couldn't reach SteamGridDB — check your connection.";
            }
        }

        /// <summary>
        /// Searches SteamGridDB for a game by name and returns a high-res portrait cover ("grid") URL,
        /// or null if there's no token/match. Used as an artwork fallback when the other sources miss.
        /// </summary>
        public async Task<string?> FetchCoverUrlAsync(string? token, string? gameName)
        {
            token = (token ?? "").Trim();
            if (string.IsNullOrEmpty(token) || string.IsNullOrWhiteSpace(gameName)) return null;
            try
            {
                int? gameId = await GetGameIdAsync(token, gameName);
                if (gameId == null) { Log($"No match for \"{gameName}\"."); return null; }

                // Portrait box-art grids, static only, prefer 600x900.
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    BaseUrl + $"grids/game/{gameId}?dimensions=600x900&types=static&nsfw=false");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.UserAgent.ParseAdd("Emutastic");

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) { Log($"grids {gameId} -> {(int)resp.StatusCode}"); return null; }
                var data = JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["data"] as JsonArray;
                string? url = data is { Count: > 0 } ? data[0]?["url"]?.GetValue<string>() : null;
                Log($"Cover for \"{gameName}\" (id {gameId}): {url ?? "none"}");
                return url;
            }
            catch (Exception ex) { Log($"FetchCoverUrl error: {ex.Message}"); return null; }
        }

        private async Task<int?> GetGameIdAsync(string token, string gameName)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                BaseUrl + "search/autocomplete/" + Uri.EscapeDataString(gameName));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.UserAgent.ParseAdd("Emutastic");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var data = JsonNode.Parse(await resp.Content.ReadAsStringAsync())?["data"] as JsonArray;
            return data is { Count: > 0 } ? data[0]?["id"]?.GetValue<int>() : null;
        }

        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
