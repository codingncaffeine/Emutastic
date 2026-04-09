using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Fetches video snaps from screenscraper.fr API v2.
    /// Credentials are the user's own screenscraper.fr account — no developer
    /// registration required for personal use.
    /// </summary>
    public class ScreenScraperService
    {
        private const string BaseUrl    = "https://www.screenscraper.fr/api2/";
        private const string SoftName   = "Emutastic";
        private const string DevId      = "stragee";
        private const string DevPass    = "2ixrETMUmd9";

        private readonly HttpClient _http;
        private readonly string     _snapCacheFolder;
        private readonly string     _boxArt3DCacheFolder;

        // Maps our internal console tags → ScreenScraper numeric system IDs
        private static readonly Dictionary<string, int> SystemIds = new()
        {
            { "NES",          3  },
            { "FDS",          3  },   // Famicom Disk System shares NES in SS
            { "SNES",         4  },
            { "N64",          14 },
            { "GameCube",     13 },
            { "GB",           9  },
            { "GBC",          10 },
            { "GBA",          12 },
            { "NDS",          15 },
            { "VirtualBoy",   11 },
            { "Genesis",      1  },
            { "SegaCD",       20 },
            { "Sega32X",      19 },
            { "Saturn",       22 },
            { "SMS",          2  },
            { "GameGear",     21 },
            { "SG1000",       25 },
            { "Dreamcast",    23 },
            { "PS1",          57 },
            { "PSP",          61 },
            { "TG16",         31 },
            { "TGCD",         114},
            { "NGP",          69 },
            { "Atari2600",    26 },

            { "Atari7800",    41 },
            { "Jaguar",       27 },
            { "ColecoVision", 48 },

            { "Vectrex",      102},
            { "3DO",          29 },
            { "Arcade",       75 },
            { "NeoGeo",       142},
            { "CDi",          133},
            { "Odyssey2",     104},
        };

        public ScreenScraperService()
        {
            _snapCacheFolder = AppPaths.GetFolder("Snaps");
            _boxArt3DCacheFolder = AppPaths.GetFolder("BoxArt3D");

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("User-Agent", $"{SoftName}/1.0");
        }

        /// <summary>
        /// Tests credentials. Returns null on success, or an error string to display to the user.
        /// </summary>
        public async Task<string?> TestLoginAsync(string username, string password)
        {
            try
            {
                string url = $"{BaseUrl}ssuserInfos.php" +
                             $"?devid={Uri.EscapeDataString(DevId)}" +
                             $"&devpassword={Uri.EscapeDataString(DevPass)}" +
                             $"&softname={Uri.EscapeDataString(SoftName)}" +
                             $"&output=json" +
                             $"&ssid={Uri.EscapeDataString(username)}" +
                             $"&sspassword={Uri.EscapeDataString(password)}";

                var response = await _http.GetAsync(url);
                string json  = await response.Content.ReadAsStringAsync();

                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Login response ({(int)response.StatusCode}): {json}");

                if (!response.IsSuccessStatusCode)
                    return $"Server returned {(int)response.StatusCode}";

                var doc = JsonNode.Parse(json);

                // Check header.success first — SS returns 200 even for auth failures
                string? headerSuccess = doc?["header"]?["success"]?.GetValue<string>();
                if (headerSuccess == "false")
                {
                    string? error = doc?["header"]?["error"]?.GetValue<string>();
                    return string.IsNullOrWhiteSpace(error) ? "Login failed" : error;
                }

                // Accept either response shape (with or without "response" wrapper)
                bool hasUser = doc?["response"]?["ssuser"] != null
                            || doc?["ssuser"] != null;

                return hasUser ? null : "Login failed — unexpected response format";
            }
            catch (Exception ex)
            {
                return $"Connection error: {ex.Message}";
            }
        }

        /// <summary>
        /// Returns the local path to a cached .mp4 snap, or null if not found / not yet fetched.
        /// </summary>
        public string? FindCachedSnap(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey)) return null;
            string path = Path.Combine(_snapCacheFolder, $"{cacheKey}.mp4");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Queries ScreenScraper for a video snap URL then downloads it.
        /// Searches by MD5 hash first, falls back to filename + system.
        /// Returns local .mp4 path on success, null otherwise.
        /// </summary>
        public async Task<string?> FetchSnapAsync(
            string username, string password,
            string console,  string romHash,
            string romPath)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            if (!SystemIds.TryGetValue(console, out int systemId)) return null;

            string cacheKey = string.IsNullOrWhiteSpace(romHash)
                ? Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(romPath)))
                : romHash;

            // Cache hit
            string? cached = FindCachedSnap(cacheKey);
            if (cached != null) return cached;

            try
            {
                string auth   = $"devid={Uri.EscapeDataString(DevId)}&devpassword={Uri.EscapeDataString(DevPass)}" +
                                $"&softname={Uri.EscapeDataString(SoftName)}&output=json" +
                                $"&ssid={Uri.EscapeDataString(username)}&sspassword={Uri.EscapeDataString(password)}";
                string romName = Uri.EscapeDataString(Path.GetFileName(romPath));
                string md5Part = string.IsNullOrWhiteSpace(romHash)
                    ? ""
                    : $"&md5={romHash.ToUpperInvariant()}";

                string url = $"{BaseUrl}jeuInfos.php?{auth}&systemeid={systemId}{md5Part}&romnom={romName}";

                var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json    = await response.Content.ReadAsStringAsync();
                string? snapUrl = ExtractVideoUrl(json);
                if (snapUrl == null) return null;

                return await DownloadSnapAsync(snapUrl, cacheKey);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] FetchSnap failed: {ex.Message}");
                return null;
            }
        }

        private static string? ExtractVideoUrl(string json)
        {
            try
            {
                var doc    = JsonNode.Parse(json);
                var medias = doc?["response"]?["jeu"]?["medias"]?.AsArray();
                if (medias == null) return null;

                // Prefer "video-normalized" (smaller, consistent quality), fall back to "video"
                string? normalizedUrl = null;
                string? regularUrl   = null;

                foreach (var media in medias)
                {
                    string? type = media?["type"]?.GetValue<string>();
                    string? mediaUrl = media?["url"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(mediaUrl)) continue;

                    if (type == "video-normalized") normalizedUrl = mediaUrl;
                    else if (type == "video")        regularUrl   = mediaUrl;
                }

                return normalizedUrl ?? regularUrl;
            }
            catch { return null; }
        }

        private async Task<string?> DownloadSnapAsync(string snapUrl, string cacheKey)
        {
            try
            {
                string localPath = Path.Combine(_snapCacheFolder, $"{cacheKey}.mp4");
                var snapResponse = await _http.GetAsync(snapUrl);
                if (!snapResponse.IsSuccessStatusCode) return null;

                byte[] bytes = await snapResponse.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(localPath, bytes);
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Snap saved: {localPath}");
                return localPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// Result from a box art fetch — includes quota/error info for status display.
        /// </summary>
        public class BoxArt3DResult
        {
            public string? LocalPath { get; set; }
            public bool    OverQuota { get; set; }
            public string? ErrorMessage { get; set; }
        }

        /// <summary>
        /// Fetches 3D box art image from ScreenScraper.
        /// Returns path on success, or error/quota info on failure.
        /// </summary>
        public async Task<BoxArt3DResult> FetchBoxArt3DAsync(
            string username, string password,
            string console, string romHash, string romPath)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return new BoxArt3DResult { ErrorMessage = "ScreenScraper not configured" };

            if (!SystemIds.TryGetValue(console, out int systemId))
                return new BoxArt3DResult { ErrorMessage = $"Console '{console}' not supported" };

            string cacheKey = string.IsNullOrWhiteSpace(romHash)
                ? Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(romPath)))
                : romHash;

            // Cache hit
            string cached = Path.Combine(_boxArt3DCacheFolder, $"{cacheKey}.png");
            if (File.Exists(cached))
                return new BoxArt3DResult { LocalPath = cached };

            try
            {
                string auth = $"devid={Uri.EscapeDataString(DevId)}&devpassword={Uri.EscapeDataString(DevPass)}" +
                              $"&softname={Uri.EscapeDataString(SoftName)}&output=json" +
                              $"&ssid={Uri.EscapeDataString(username)}&sspassword={Uri.EscapeDataString(password)}";
                string romName = Uri.EscapeDataString(Path.GetFileName(romPath));
                string md5Part = string.IsNullOrWhiteSpace(romHash)
                    ? ""
                    : $"&md5={romHash.ToUpperInvariant()}";

                string url = $"{BaseUrl}jeuInfos.php?{auth}&systemeid={systemId}{md5Part}&romnom={romName}";

                var response = await _http.GetAsync(url);
                string json = await response.Content.ReadAsStringAsync();
                int statusCode = (int)response.StatusCode;

                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] 3D art response: HTTP {statusCode}, {json.Length} bytes");

                // Check for quota exceeded — SS returns 430 for over-quota
                if (statusCode == 430 || statusCode == 423)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Quota exceeded (HTTP {statusCode})");
                    return new BoxArt3DResult { OverQuota = true, ErrorMessage = "ScreenScraper daily request limit reached" };
                }

                // SS sometimes returns 200 with an error body instead of game data
                if (json.Contains("API closed", StringComparison.OrdinalIgnoreCase) ||
                    json.Contains("maxrequestsreached", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Quota exceeded (body): {json[..Math.Min(200, json.Length)]}");
                    return new BoxArt3DResult { OverQuota = true, ErrorMessage = "ScreenScraper daily request limit reached" };
                }

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Non-success: HTTP {statusCode} — {json[..Math.Min(300, json.Length)]}");
                    return new BoxArt3DResult { ErrorMessage = $"Server returned {statusCode}" };
                }

                string? imageUrl = ExtractBoxArt3DUrl(json);
                if (imageUrl == null)
                    return new BoxArt3DResult(); // No 3D art available — not an error

                // Download the image
                var imgResponse = await _http.GetAsync(imageUrl);
                if (!imgResponse.IsSuccessStatusCode)
                    return new BoxArt3DResult();

                byte[] bytes = await imgResponse.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(cached, bytes);
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] 3D box art saved: {cached}");
                return new BoxArt3DResult { LocalPath = cached };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] FetchBoxArt3D failed: {ex.Message}");
                return new BoxArt3DResult { ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// Fetches 2D box art from ScreenScraper. Used as a fallback when libretro thumbnails miss.
        /// Returns local image path on success, null otherwise.
        /// </summary>
        public async Task<string?> FetchBoxArt2DAsync(
            string username, string password,
            string console, string romHash, string romPath)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            if (!SystemIds.TryGetValue(console, out int systemId)) return null;

            string cacheKey = string.IsNullOrWhiteSpace(romHash)
                ? Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(romPath)))
                : romHash;

            // Cache hit
            string cached = Path.Combine(_boxArt3DCacheFolder, $"{cacheKey}_2d.png");
            if (File.Exists(cached)) return cached;

            try
            {
                string auth = $"devid={Uri.EscapeDataString(DevId)}&devpassword={Uri.EscapeDataString(DevPass)}" +
                              $"&softname={Uri.EscapeDataString(SoftName)}&output=json" +
                              $"&ssid={Uri.EscapeDataString(username)}&sspassword={Uri.EscapeDataString(password)}";
                string romName = Uri.EscapeDataString(Path.GetFileName(romPath));
                string md5Part = string.IsNullOrWhiteSpace(romHash)
                    ? ""
                    : $"&md5={romHash.ToUpperInvariant()}";

                string url = $"{BaseUrl}jeuInfos.php?{auth}&systemeid={systemId}{md5Part}&romnom={romName}";

                var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                string? imageUrl = ExtractBoxArt2DUrl(json);
                if (imageUrl == null) return null;

                var imgResponse = await _http.GetAsync(imageUrl);
                if (!imgResponse.IsSuccessStatusCode) return null;

                byte[] bytes = await imgResponse.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(cached, bytes);
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] 2D box art saved: {cached}");
                return cached;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] FetchBoxArt2D failed: {ex.Message}");
                return null;
            }
        }

        private static string? ExtractBoxArt2DUrl(string json)
        {
            try
            {
                var doc = JsonNode.Parse(json);
                var medias = doc?["response"]?["jeu"]?["medias"]?.AsArray();
                if (medias == null) return null;

                string? us = null, eu = null, wor = null, jp = null, generic = null;

                foreach (var media in medias)
                {
                    string? type = media?["type"]?.GetValue<string>();
                    string? mediaUrl = media?["url"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(mediaUrl)) continue;

                    if (type == "box-2D-us" || type == "box-2D-USA")       us = mediaUrl;
                    else if (type == "box-2D-eu" || type == "box-2D-EUR")  eu = mediaUrl;
                    else if (type == "box-2D-wor")                          wor = mediaUrl;
                    else if (type == "box-2D-jp" || type == "box-2D-JAP")  jp = mediaUrl;
                    else if (type == "box-2D")                              generic = mediaUrl;
                }

                return us ?? wor ?? eu ?? jp ?? generic;
            }
            catch { return null; }
        }

        private static string? ExtractBoxArt3DUrl(string json)
        {
            try
            {
                var doc = JsonNode.Parse(json);
                var medias = doc?["response"]?["jeu"]?["medias"]?.AsArray();
                if (medias == null) return null;

                // Prefer region-specific: us → eu → wor → jp, then generic box-3D
                string? us = null, eu = null, wor = null, jp = null, generic = null;

                foreach (var media in medias)
                {
                    string? type = media?["type"]?.GetValue<string>();
                    string? mediaUrl = media?["url"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(mediaUrl)) continue;

                    if (type == "box-3D-us" || type == "box-3D-USA")       us = mediaUrl;
                    else if (type == "box-3D-eu" || type == "box-3D-EUR")  eu = mediaUrl;
                    else if (type == "box-3D-wor")                          wor = mediaUrl;
                    else if (type == "box-3D-jp" || type == "box-3D-JAP")  jp = mediaUrl;
                    else if (type == "box-3D")                              generic = mediaUrl;
                }

                return us ?? wor ?? eu ?? jp ?? generic;
            }
            catch { return null; }
        }
    }
}
