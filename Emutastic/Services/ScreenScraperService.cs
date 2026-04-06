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
        private const string DevId      = "";
        private const string DevPass    = "";

        private readonly HttpClient _http;
        private readonly string     _snapCacheFolder;

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
        };

        public ScreenScraperService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _snapCacheFolder = Path.Combine(appData, "Emutastic", "Snaps");
            Directory.CreateDirectory(_snapCacheFolder);

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
                // Use the username as the devid — acceptable for personal-use apps
                // when no official developer registration exists.
                string url = $"{BaseUrl}ssuserInfos.php" +
                             $"?softname={Uri.EscapeDataString(SoftName)}" +
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
                string auth   = $"softname={Uri.EscapeDataString(SoftName)}&output=json" +
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
    }
}
