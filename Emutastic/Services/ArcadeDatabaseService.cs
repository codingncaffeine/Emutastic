using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Lightweight client for adb.arcadeitalia.net — a free, no-auth metadata
    /// service keyed by MAME (and by extension FBNeo) ROM shortname. Returns
    /// year, manufacturer, genre, history (synopsis), and image URLs for arcade
    /// titles. Used as the default metadata source for the Arcade and Neo Geo
    /// libraries when the user hasn't configured ScreenScraper, and as a
    /// fallback when ScreenScraper is configured but doesn't return a hit.
    ///
    /// Per the service's terms, attribution ("Data: arcade-database.com /
    /// arcade-history.com") should be displayed in the game-detail card when
    /// data came from this source.
    /// </summary>
    public class ArcadeDatabaseService
    {
        private const string BaseUrl = "https://adb.arcadeitalia.net/service_scraper.php";

        public record AdbResult(
            string ShortName,
            string Title,
            string? Year,
            string? Manufacturer,
            string? Genre,
            string? History,
            string? MarqueeUrl,
            string? FlyerUrl,
            string? CabinetUrl,
            string? TitleScreenUrl,
            string? InGameUrl,
            string? YoutubeId);

        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            c.DefaultRequestHeaders.Add("User-Agent", "Emutastic/1.x (+https://github.com/codingncaffeine/Emutastic)");
            return c;
        }

        /// <summary>
        /// Fetches metadata for the given MAME/FBNeo shortname. Pass `use_parent=1`
        /// so clone shortnames (mslugu → mslug, kof98n → kof98) resolve to the
        /// parent set automatically — keeps coverage high without callers having
        /// to know clone relationships.
        /// </summary>
        public async Task<AdbResult?> FetchAsync(string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName)) return null;
            try
            {
                string url = $"{BaseUrl}?ajax=query_mame&game_name={Uri.EscapeDataString(shortName)}&use_parent=1";
                using var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                string json = await resp.Content.ReadAsStringAsync();
                return Parse(json, shortName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ADB] FetchAsync({shortName}) failed: {ex.Message}");
                return null;
            }
        }

        private static AdbResult? Parse(string json, string shortName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var resultArr)
                    || resultArr.ValueKind != JsonValueKind.Array
                    || resultArr.GetArrayLength() == 0)
                    return null;

                var first = resultArr[0];
                string Get(string field) =>
                    first.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? ""
                        : "";

                string title = Get("title");
                if (string.IsNullOrWhiteSpace(title)) return null;

                return new AdbResult(
                    ShortName:      shortName,
                    Title:          title,
                    Year:           NullIfEmpty(Get("year")),
                    Manufacturer:   NullIfEmpty(Get("manufacturer")),
                    Genre:          NullIfEmpty(Get("genre")),
                    History:        NullIfEmpty(Get("history")),
                    MarqueeUrl:     NullIfEmpty(Get("url_image_marquee")),
                    FlyerUrl:       NullIfEmpty(Get("url_image_flyer")),
                    CabinetUrl:     NullIfEmpty(Get("url_image_cabinet")),
                    TitleScreenUrl: NullIfEmpty(Get("url_image_title")),
                    InGameUrl:      NullIfEmpty(Get("url_image_ingame")),
                    YoutubeId:      NullIfEmpty(Get("youtube_video_id")));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[ADB] Parse failed: {ex.Message}");
                return null;
            }
        }

        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
