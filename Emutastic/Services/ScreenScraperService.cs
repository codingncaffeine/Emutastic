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
        private const string DevPass    = "REDACTED";

        private readonly HttpClient _http;
        private readonly string     _snapCacheFolder;
        private readonly string     _boxArt3DCacheFolder;

        // Shared throttle — limits concurrent ScreenScraper API requests across all callers.
        // Configured from the user's maxthreads value returned by ssuserInfos.
        private static System.Threading.SemaphoreSlim _throttle = new(1, 1);
        private static int _currentMaxThreads = 1;

        // Session-sticky flag: once SS returns a quota-exhausted response, every
        // subsequent call returns null immediately so callers fall through to
        // their fallback path (e.g. ArcadeDatabase) instead of burning more
        // round-trips that we already know will fail. Resets on app restart.
        private static volatile bool _quotaExhausted;

        /// <summary>
        /// True when a previous SS call returned a quota-exhausted response
        /// (HTTP 423/430 or "API closed" / "maxrequestsreached" body marker).
        /// Callers can use this to short-circuit straight to a fallback source.
        /// </summary>
        public static bool QuotaExhausted => _quotaExhausted;

        /// <summary>
        /// Sets the maximum concurrent ScreenScraper API requests based on the user's account tier.
        /// </summary>
        public static void SetMaxThreads(int maxThreads)
        {
            maxThreads = Math.Max(1, maxThreads);
            if (maxThreads == _currentMaxThreads) return;
            _currentMaxThreads = maxThreads;
            _throttle = new System.Threading.SemaphoreSlim(maxThreads, maxThreads);
            System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Throttle set to {maxThreads} threads");
        }

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
            { "3DS",          17 },
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
            { "NeoCD",         70},
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
        /// Throttled HTTP GET — acquires a slot from the shared semaphore before making the request.
        /// The semaphore counts concurrent in-flight requests across all callers/instances.
        /// </summary>
        private Task<HttpResponseMessage> ThrottledGetAsync(string url)
        {
            // Throttle is now handled by the caller's semaphore so each game fetch
            // (which may make multiple HTTP calls) counts as one concurrent slot.
            return _http.GetAsync(url);
        }

        /// <summary>Current max threads for display purposes.</summary>
        public static int CurrentMaxThreads => _currentMaxThreads;

        /// <summary>
        /// Tests credentials. Returns null on success, or an error string to display to the user.
        /// </summary>
        public async Task<(string? error, int maxThreads)> TestLoginAsync(string username, string password)
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
                    return ($"Server returned {(int)response.StatusCode}", 1);

                var doc = JsonNode.Parse(json);

                // Check header.success first — SS returns 200 even for auth failures
                string? headerSuccess = doc?["header"]?["success"]?.GetValue<string>();
                if (headerSuccess == "false")
                {
                    string? error = doc?["header"]?["error"]?.GetValue<string>();
                    return (string.IsNullOrWhiteSpace(error) ? "Login failed" : error, 1);
                }

                // Accept either response shape (with or without "response" wrapper)
                var ssuser = doc?["response"]?["ssuser"] ?? doc?["ssuser"];
                if (ssuser == null)
                    return ("Login failed — unexpected response format", 1);

                // Parse maxthreads from user info (defaults to 1 for free users)
                int maxThreads = 1;
                string? maxThreadsStr = ssuser["maxthreads"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(maxThreadsStr) && int.TryParse(maxThreadsStr, out int parsed) && parsed > 0)
                    maxThreads = parsed;

                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] User {username}: maxthreads={maxThreads}");
                return (null, maxThreads);
            }
            catch (Exception ex)
            {
                return ($"Connection error: {ex.Message}", 1);
            }
        }

        /// <summary>
        /// Returns the local path to a cached .mp4 snap, or null if not found / not yet fetched.
        /// </summary>
        public string? FindCachedSnap(string cacheKey, string? console = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey)) return null;
            // Check console subfolder first
            if (!string.IsNullOrWhiteSpace(console))
            {
                string consolePath = Path.Combine(AppPaths.GetFolder("Snaps", console), $"{cacheKey}.mp4");
                if (File.Exists(consolePath)) return consolePath;
            }
            // Fall back to flat folder (pre-migration files)
            string path = Path.Combine(_snapCacheFolder, $"{cacheKey}.mp4");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Builds <c>romnom</c> search candidates sent to ScreenScraper, in priority
        /// order. DOS games are catalogued by zipped-folder names, so .exe filenames
        /// never match — walk up past drive-letter and bulk-dir shadow folders.
        /// ScreenScraper's fuzzy matcher doesn't bridge Arabic↔Roman numerals
        /// ("Dungeon Master 2" vs "Dungeon Master II"), so we emit both forms.
        /// </summary>
        private static IEnumerable<string> BuildRomNomCandidates(string console, string romPath)
        {
            yield return Path.GetFileName(romPath);
        }

        private static readonly Dictionary<string, string> NumeralArabicToRoman = new()
        {
            ["1"] = "I", ["2"] = "II", ["3"] = "III", ["4"] = "IV", ["5"] = "V",
            ["6"] = "VI", ["7"] = "VII", ["8"] = "VIII", ["9"] = "IX", ["10"] = "X",
        };
        private static readonly Dictionary<string, string> NumeralRomanToArabic = new(StringComparer.OrdinalIgnoreCase)
        {
            ["i"] = "1", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5",
            ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
        };

        private static string SwapNumeralStyle(string input, bool toRoman)
        {
            if (toRoman)
            {
                return System.Text.RegularExpressions.Regex.Replace(input, @"\b(10|[1-9])\b",
                    m => NumeralArabicToRoman.TryGetValue(m.Value, out var r) ? r : m.Value);
            }
            return System.Text.RegularExpressions.Regex.Replace(input, @"\b(viii|vii|iii|ix|iv|vi|ii|x|v|i)\b",
                m => NumeralRomanToArabic.TryGetValue(m.Value, out var a) ? a : m.Value,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static readonly HashSet<string> DosShadowFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "dos", "dos games", "doslib", "games", "game", "pc", "pcgames",
            "bin", "program files", "programs",
        };

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

            // Cache hit — check console subfolder first, then flat
            string? cached = FindCachedSnap(cacheKey, console);
            if (cached != null) return cached;

            try
            {
                string auth   = $"devid={Uri.EscapeDataString(DevId)}&devpassword={Uri.EscapeDataString(DevPass)}" +
                                $"&softname={Uri.EscapeDataString(SoftName)}&output=json" +
                                $"&ssid={Uri.EscapeDataString(username)}&sspassword={Uri.EscapeDataString(password)}";
                string md5Part = string.IsNullOrWhiteSpace(romHash)
                    ? ""
                    : $"&md5={romHash.ToUpperInvariant()}";
                // ScreenScraper uses taillerom (filesize in bytes) as a strong matcher,
                // especially for large console ROMs where the MD5 path
                // is unreliable. Cheap to compute, helps SS resolve by filename alone.
                try
                {
                    if (!string.IsNullOrEmpty(romPath) && System.IO.File.Exists(romPath))
                        md5Part += $"&taillerom={new System.IO.FileInfo(romPath).Length}";
                }
                catch { /* size lookup failure is non-fatal */ }

                foreach (string candidate in BuildRomNomCandidates(console, romPath))
                {
                    string romName = Uri.EscapeDataString(candidate);
                    string url = $"{BaseUrl}jeuInfos.php?{auth}&systemeid={systemId}{md5Part}&romnom={romName}";

                    var response = await ThrottledGetAsync(url);
                    if (!response.IsSuccessStatusCode) continue;

                    string json    = await response.Content.ReadAsStringAsync();
                    string? snapUrl = ExtractVideoUrl(json);
                    if (snapUrl == null) continue;

                    return await DownloadSnapAsync(snapUrl, cacheKey, console);
                }
                return null;
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

        private async Task<string?> DownloadSnapAsync(string snapUrl, string cacheKey, string? console = null)
        {
            try
            {
                string folder = !string.IsNullOrWhiteSpace(console)
                    ? AppPaths.GetFolder("Snaps", console)
                    : _snapCacheFolder;
                string localPath = Path.Combine(folder, $"{cacheKey}.mp4");
                var snapResponse = await ThrottledGetAsync(snapUrl);
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

            // Cache hit — check console subfolder first, then flat
            string consoleFolder = AppPaths.GetFolder("BoxArt3D", console);
            string cached = Path.Combine(consoleFolder, $"{cacheKey}.png");
            if (File.Exists(cached))
                return new BoxArt3DResult { LocalPath = cached };
            // Fall back to flat folder (pre-migration files)
            string flatCached = Path.Combine(_boxArt3DCacheFolder, $"{cacheKey}.png");
            if (File.Exists(flatCached))
                return new BoxArt3DResult { LocalPath = flatCached };

            try
            {
                string auth = $"devid={Uri.EscapeDataString(DevId)}&devpassword={Uri.EscapeDataString(DevPass)}" +
                              $"&softname={Uri.EscapeDataString(SoftName)}&output=json" +
                              $"&ssid={Uri.EscapeDataString(username)}&sspassword={Uri.EscapeDataString(password)}";
                string md5Part = string.IsNullOrWhiteSpace(romHash)
                    ? ""
                    : $"&md5={romHash.ToUpperInvariant()}";
                // ScreenScraper uses taillerom (filesize in bytes) as a strong matcher,
                // especially for large console ROMs where the MD5 path
                // is unreliable. Cheap to compute, helps SS resolve by filename alone.
                try
                {
                    if (!string.IsNullOrEmpty(romPath) && System.IO.File.Exists(romPath))
                        md5Part += $"&taillerom={new System.IO.FileInfo(romPath).Length}";
                }
                catch { /* size lookup failure is non-fatal */ }

                foreach (string candidate in BuildRomNomCandidates(console, romPath))
                {
                    string romName = Uri.EscapeDataString(candidate);
                    string url = $"{BaseUrl}jeuInfos.php?{auth}&systemeid={systemId}{md5Part}&romnom={romName}";

                    var response = await ThrottledGetAsync(url);
                    string json = await response.Content.ReadAsStringAsync();
                    int statusCode = (int)response.StatusCode;

                    System.Diagnostics.Debug.WriteLine($"[ScreenScraper] 3D art response ('{candidate}'): HTTP {statusCode}, {json.Length} bytes");

                    if (statusCode == 430 || statusCode == 423)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Quota exceeded (HTTP {statusCode})");
                        return new BoxArt3DResult { OverQuota = true, ErrorMessage = "ScreenScraper daily request limit reached" };
                    }

                    if (json.Contains("API closed", StringComparison.OrdinalIgnoreCase) ||
                        json.Contains("maxrequestsreached", StringComparison.OrdinalIgnoreCase))
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Quota exceeded (body): {json[..Math.Min(200, json.Length)]}");
                        return new BoxArt3DResult { OverQuota = true, ErrorMessage = "ScreenScraper daily request limit reached" };
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Non-success: HTTP {statusCode} — {json[..Math.Min(300, json.Length)]}");
                        continue; // try next romnom variant
                    }

                    string? imageUrl = ExtractBoxArt3DUrl(json);
                    if (imageUrl == null)
                        continue; // no art for this variant — try next

                    var imgResponse = await ThrottledGetAsync(imageUrl);
                    if (!imgResponse.IsSuccessStatusCode)
                        continue;

                    byte[] bytes = await imgResponse.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(cached, bytes);
                    System.Diagnostics.Debug.WriteLine($"[ScreenScraper] 3D box art saved: {cached}");
                    return new BoxArt3DResult { LocalPath = cached };
                }
                return new BoxArt3DResult(); // tried all variants, no match
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

            // Cache hit — check ss2d console subfolder first, then legacy BoxArt3D location
            string consoleFolder2D = AppPaths.GetFolder("ss2d", console);
            string cached = Path.Combine(consoleFolder2D, $"{cacheKey}.png");
            if (File.Exists(cached)) return cached;
            // Legacy location (before ss2d folder existed)
            string legacyFolder = AppPaths.GetFolder("BoxArt3D", console);
            string legacyCached = Path.Combine(legacyFolder, $"{cacheKey}_2d.png");
            if (File.Exists(legacyCached)) return legacyCached;
            string flatCached2D = Path.Combine(_boxArt3DCacheFolder, $"{cacheKey}_2d.png");
            if (File.Exists(flatCached2D)) return flatCached2D;

            try
            {
                string auth = $"devid={Uri.EscapeDataString(DevId)}&devpassword={Uri.EscapeDataString(DevPass)}" +
                              $"&softname={Uri.EscapeDataString(SoftName)}&output=json" +
                              $"&ssid={Uri.EscapeDataString(username)}&sspassword={Uri.EscapeDataString(password)}";
                string md5Part = string.IsNullOrWhiteSpace(romHash)
                    ? ""
                    : $"&md5={romHash.ToUpperInvariant()}";
                // ScreenScraper uses taillerom (filesize in bytes) as a strong matcher,
                // especially for large console ROMs where the MD5 path
                // is unreliable. Cheap to compute, helps SS resolve by filename alone.
                try
                {
                    if (!string.IsNullOrEmpty(romPath) && System.IO.File.Exists(romPath))
                        md5Part += $"&taillerom={new System.IO.FileInfo(romPath).Length}";
                }
                catch { /* size lookup failure is non-fatal */ }

                foreach (string candidate in BuildRomNomCandidates(console, romPath))
                {
                    string romName = Uri.EscapeDataString(candidate);
                    string url = $"{BaseUrl}jeuInfos.php?{auth}&systemeid={systemId}{md5Part}&romnom={romName}";

                    var response = await ThrottledGetAsync(url);
                    if (!response.IsSuccessStatusCode) continue;

                    string json = await response.Content.ReadAsStringAsync();
                    string? imageUrl = ExtractBoxArt2DUrl(json);
                    if (imageUrl == null) continue;

                    var imgResponse = await ThrottledGetAsync(imageUrl);
                    if (!imgResponse.IsSuccessStatusCode) continue;

                    byte[] bytes = await imgResponse.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(cached, bytes);
                    System.Diagnostics.Debug.WriteLine($"[ScreenScraper] 2D box art saved: {cached}");
                    return cached;
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] FetchBoxArt2D failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Metadata fields parsed from a ScreenScraper jeuInfos response.</summary>
        public record SsMetadata(
            string? Title,
            string? Year,
            string? Developer,
            string? Publisher,
            string? Genre,
            string? Description);

        /// <summary>
        /// Fetches game metadata (year, developer, publisher, genre, synopsis)
        /// for a ROM from screenscraper.fr. Reuses the same jeuInfos.php endpoint
        /// the art-fetch path already calls — no extra API surface. Returns null
        /// if credentials are missing, the console isn't supported, or no match
        /// was found across the romnom candidates.
        /// </summary>
        public async Task<SsMetadata?> FetchMetadataAsync(
            string username, string password,
            string console, string romHash, string romPath)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;
            if (!SystemIds.TryGetValue(console, out int systemId))
                return null;
            // Session-sticky quota guard — once exhausted, every subsequent call
            // short-circuits so the caller's fallback path (ADB) takes over.
            if (_quotaExhausted) return null;

            try
            {
                string auth = $"devid={Uri.EscapeDataString(DevId)}&devpassword={Uri.EscapeDataString(DevPass)}" +
                              $"&softname={Uri.EscapeDataString(SoftName)}&output=json" +
                              $"&ssid={Uri.EscapeDataString(username)}&sspassword={Uri.EscapeDataString(password)}";
                string md5Part = string.IsNullOrWhiteSpace(romHash)
                    ? ""
                    : $"&md5={romHash.ToUpperInvariant()}";
                // ScreenScraper uses taillerom (filesize in bytes) as a strong matcher,
                // especially for large console ROMs where the MD5 path
                // is unreliable. Cheap to compute, helps SS resolve by filename alone.
                try
                {
                    if (!string.IsNullOrEmpty(romPath) && System.IO.File.Exists(romPath))
                        md5Part += $"&taillerom={new System.IO.FileInfo(romPath).Length}";
                }
                catch { /* size lookup failure is non-fatal */ }

                foreach (string candidate in BuildRomNomCandidates(console, romPath))
                {
                    string romName = Uri.EscapeDataString(candidate);
                    string url = $"{BaseUrl}jeuInfos.php?{auth}&systemeid={systemId}{md5Part}&romnom={romName}";

                    var response = await ThrottledGetAsync(url);
                    int statusCode = (int)response.StatusCode;

                    // Quota markers — match what FetchBoxArt3DAsync checks.
                    if (statusCode == 430 || statusCode == 423)
                    {
                        _quotaExhausted = true;
                        System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Quota exceeded (HTTP {statusCode}) — switching to fallback");
                        return null;
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    if (json.Contains("API closed", StringComparison.OrdinalIgnoreCase) ||
                        json.Contains("maxrequestsreached", StringComparison.OrdinalIgnoreCase))
                    {
                        _quotaExhausted = true;
                        System.Diagnostics.Debug.WriteLine($"[ScreenScraper] Quota exceeded (body marker) — switching to fallback");
                        return null;
                    }

                    if (!response.IsSuccessStatusCode) continue;

                    var parsed = ExtractMetadata(json);
                    if (parsed != null) return parsed;
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] FetchMetadata failed: {ex.Message}");
                return null;
            }
        }

        private static SsMetadata? ExtractMetadata(string json)
        {
            try
            {
                var doc = JsonNode.Parse(json);
                var jeu = doc?["response"]?["jeu"];
                if (jeu == null) return null;

                // SS returns regional name arrays: prefer us → wor → ss → first available.
                string? title = PickRegional(jeu["noms"]?.AsArray(), "text", new[] { "us", "wor", "ss", "eu", "jp" });

                // Dates same structure as names.
                string? year = PickRegional(jeu["dates"]?.AsArray(), "text", new[] { "us", "wor", "ss", "eu", "jp" });
                // Strip to just the year if it's a full date like "1996-04-19"
                if (!string.IsNullOrEmpty(year) && year.Length >= 4 && int.TryParse(year[..4], out _))
                    year = year[..4];

                string? developer = jeu["developpeur"]?["text"]?.GetValue<string>();
                string? publisher = jeu["editeur"]?["text"]?.GetValue<string>();

                // Genre is an array of {noms: [{langue, text}]}. Pick first genre, prefer English.
                string? genre = null;
                var genres = jeu["genres"]?.AsArray();
                if (genres != null && genres.Count > 0)
                {
                    var firstGenre = genres[0];
                    genre = PickRegional(firstGenre?["noms"]?.AsArray(), "text", new[] { "en", "us", "wor" }, langField: "langue");
                }

                // Synopsis: array of {langue, text}. Prefer English.
                string? description = PickRegional(jeu["synopsis"]?.AsArray(), "text", new[] { "en", "us", "wor" }, langField: "langue");

                // If nothing useful came back, treat as no-match.
                if (string.IsNullOrWhiteSpace(title)
                    && string.IsNullOrWhiteSpace(year)
                    && string.IsNullOrWhiteSpace(developer)
                    && string.IsNullOrWhiteSpace(publisher)
                    && string.IsNullOrWhiteSpace(genre)
                    && string.IsNullOrWhiteSpace(description))
                    return null;

                return new SsMetadata(
                    NullIfEmpty(title),
                    NullIfEmpty(year),
                    NullIfEmpty(developer),
                    NullIfEmpty(publisher),
                    NullIfEmpty(genre),
                    NullIfEmpty(description));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenScraper] ExtractMetadata failed: {ex.Message}");
                return null;
            }
        }

        // Helper: walk a regional-array of objects, prefer entries whose region
        // field matches one of the preferred values, return the requested
        // text field. Falls back to the first available entry's text.
        private static string? PickRegional(JsonArray? arr, string textField, string[] preferred, string langField = "region")
        {
            if (arr == null || arr.Count == 0) return null;
            foreach (string pref in preferred)
            {
                foreach (var entry in arr)
                {
                    string? regionValue = entry?[langField]?.GetValue<string>();
                    if (string.Equals(regionValue, pref, StringComparison.OrdinalIgnoreCase))
                    {
                        string? text = entry?[textField]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            // No regional match — return the first non-empty text.
            foreach (var entry in arr)
            {
                string? text = entry?[textField]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return null;
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        // Region preference: us → wor → eu → jp → anything else.
        // Handles BOTH SS schemas:
        //   (a) legacy concatenated types: "box-2D-us", "box-2D-USA", "box-2D-eu", ...
        //   (b) modern split fields (most post-2020 entries):
        //       "type": "box-2D", "region": "us"
        private static string? PickRegionalMediaUrl(System.Text.Json.Nodes.JsonArray medias, string baseType)
        {
            string? us = null, eu = null, wor = null, jp = null, generic = null;

            foreach (var media in medias)
            {
                string? type = media?["type"]?.GetValue<string>();
                string? mediaUrl = media?["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(mediaUrl)) continue;

                // Region from separate field (modern schema) or suffix on type (legacy)
                string? region = null;
                try { region = media?["region"]?.GetValue<string>()?.ToLowerInvariant(); } catch { }

                if (type == baseType)
                {
                    switch (region)
                    {
                        case "us":  case "usa":           us  ??= mediaUrl; break;
                        case "eu":  case "eur": case "de":
                        case "fr":  case "it":  case "es":
                        case "uk":                        eu  ??= mediaUrl; break;
                        case "wor": case "world":         wor ??= mediaUrl; break;
                        case "jp":  case "jap": case "ja":jp  ??= mediaUrl; break;
                        case null:  case "":              generic ??= mediaUrl; break;
                        default:                          generic ??= mediaUrl; break;
                    }
                }
                else if (type == $"{baseType}-us" || type == $"{baseType}-USA")
                    us ??= mediaUrl;
                else if (type == $"{baseType}-eu" || type == $"{baseType}-EUR")
                    eu ??= mediaUrl;
                else if (type == $"{baseType}-wor")
                    wor ??= mediaUrl;
                else if (type == $"{baseType}-jp" || type == $"{baseType}-JAP")
                    jp ??= mediaUrl;
            }

            return us ?? wor ?? eu ?? jp ?? generic;
        }

        private static string? ExtractBoxArt2DUrl(string json)
        {
            try
            {
                var doc = JsonNode.Parse(json);
                var medias = doc?["response"]?["jeu"]?["medias"]?.AsArray();
                if (medias == null) return null;
                return PickRegionalMediaUrl(medias, "box-2D");
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
                return PickRegionalMediaUrl(medias, "box-3D");
            }
            catch { return null; }
        }
    }
}
