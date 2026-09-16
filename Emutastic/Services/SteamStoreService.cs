using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Covers and details for Steam games added to the Windows platform through their Steam
    /// shortcut (steam://rungameid/N). Uses Steam's public store API and image CDN — no account,
    /// no key. Activity is written to Logs\steam-store.log.
    /// </summary>
    public static class SteamStoreService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Emutastic");
            return http;
        }

        /// <summary>Steam's portrait library capsules (600×900), best first. The CDN serves these
        /// under a stable name for old and new games alike.</summary>
        public static string[] CoverUrls(int appId) => new[]
        {
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
        };

        /// <summary>
        /// Name, developer, publisher, genres, short description and release year for an app, or
        /// null when the store has no page for it (or can't be reached).
        /// </summary>
        public static async Task<ArtworkResult?> FetchDetailsAsync(int appId)
        {
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"appdetails {appId} -> {(int)resp.StatusCode}");
                    return null;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty(appId.ToString(), out var app)
                    || !app.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True
                    || !app.TryGetProperty("data", out var data))
                {
                    Log($"appdetails {appId}: no store page");
                    return null;
                }

                static string First(JsonElement d, string name)
                    => d.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
                        ? WebUtility.HtmlDecode(arr[0].GetString() ?? "")
                        : "";

                string genres = data.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
                    ? string.Join(",", g.EnumerateArray()
                        .Select(x => x.TryGetProperty("description", out var dsc) ? dsc.GetString() : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s)))
                    : "";

                // Release dates are display strings ("24 Feb, 2017"); the library stores a year.
                string year = "";
                if (data.TryGetProperty("release_date", out var rel) && rel.TryGetProperty("date", out var date))
                {
                    var m = Regex.Match(date.GetString() ?? "", @"\b(19|20)\d{2}\b");
                    if (m.Success) year = m.Value;
                }

                var result = new ArtworkResult
                {
                    Title = data.TryGetProperty("name", out var n) ? WebUtility.HtmlDecode(n.GetString() ?? "") : "",
                    Developer = First(data, "developers"),
                    Publisher = First(data, "publishers"),
                    Genre = genres,
                    Description = data.TryGetProperty("short_description", out var sd)
                        ? WebUtility.HtmlDecode(sd.GetString() ?? "").Trim()
                        : "",
                    ReleaseDate = year,
                };
                Log($"appdetails {appId}: \"{result.Title}\" ({result.Developer}, {year})");
                return result;
            }
            catch (Exception ex)
            {
                Log($"appdetails {appId} failed: {ex.Message}");
                return null;
            }
        }

        internal static void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [Steam] {msg}";
            System.Diagnostics.Trace.WriteLine(line);
            try
            {
                string logPath = System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "steam-store.log");
                LogRotation.RotateIfLarge(logPath);
                System.IO.File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { /* non-fatal */ }
        }
    }
}
