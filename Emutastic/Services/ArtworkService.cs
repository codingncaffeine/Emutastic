using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public class ArtworkResult
    {
        public string Title { get; set; } = "";
        public string BoxFrontUrl { get; set; } = "";
        public string Developer { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string ReleaseDate { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public class ArtworkService
    {
        private readonly string _vgdbPath;
        private readonly string _cacheFolder;
        private readonly Dictionary<string, string> _cacheIndex;
        private readonly DatMatchService _datMatcher = new();
        // Session-level cache: "{systemFolder}/{category}" → decoded filenames (no extension)
        private readonly Dictionary<string, List<string>> _thumbnailIndex = new();

        /// <summary>
        /// If this game's artwork file is already on disk but the DB path was never saved,
        /// returns the local path so the caller can update the DB without an HTTP request.
        /// </summary>
        public string? FindCachedArtwork(string romHash)
        {
            if (string.IsNullOrWhiteSpace(romHash)) return null;
            return _cacheIndex.TryGetValue(romHash.ToLowerInvariant(), out var path) ? path : null;
        }
        private readonly HttpClient _http;

        private static readonly Dictionary<string, string> LibretroSystemMap = new()
        {
            { "NES",          "Nintendo - Nintendo Entertainment System"       },
            { "FDS",          "Nintendo - Family Computer Disk System"         },
            { "SNES",         "Nintendo - Super Nintendo Entertainment System" },
            { "N64",          "Nintendo - Nintendo 64"                         },
            { "GameCube",     "Nintendo - GameCube"                            },
            { "GB",           "Nintendo - Game Boy"                            },
            { "GBC",          "Nintendo - Game Boy Color"                      },
            { "GBA",          "Nintendo - Game Boy Advance"                    },
            { "NDS",          "Nintendo - Nintendo DS"                         },
            { "VirtualBoy",   "Nintendo - Virtual Boy"                         },
            { "Genesis",      "Sega - Mega Drive - Genesis"                    },
            { "SegaCD",       "Sega - Mega-CD - Sega CD"                       },
            { "Sega32X",      "Sega - 32X"                                     },
            { "Saturn",       "Sega - Saturn"                                  },
            { "SMS",          "Sega - Master System - Mark III"                },
            { "GameGear",     "Sega - Game Gear"                               },
            { "SG1000",       "Sega - SG-1000"                                 },
            { "Dreamcast",    "Sega - Dreamcast"                               },
            { "PS1",          "Sony - PlayStation"                             },
            { "PSP",          "Sony - PlayStation Portable"                    },
            { "TG16",         "NEC - PC Engine - TurboGrafx 16"               },
            { "TGCD",         "NEC - PC Engine CD - TurboGrafx-CD"            },
            { "NGP",          "SNK - Neo Geo Pocket"                           },
            { "NGPC",         "SNK - Neo Geo Pocket Color"                      },
            { "Atari2600",    "Atari - 2600"                                   },

            { "Atari7800",    "Atari - 7800"                                   },
            { "Jaguar",       "Atari - Jaguar"                                 },
            { "ColecoVision", "Coleco - ColecoVision"                          },

            { "Vectrex",      "GCE - Vectrex"                                  },
            { "3DO",          "The 3DO Company - 3DO"                          },
            { "Arcade",       "FBNeo - Arcade Games"                           },
        };

        // Consoles whose thumbnails may live in more than one libretro folder.
        // The primary entry in LibretroSystemMap is tried first; fallbacks come after.
        private static readonly Dictionary<string, string[]> SystemFolderFallbacks = new()
        {
            { "NGP", new[] { "SNK - Neo Geo Pocket Color" } },
        };

        private IEnumerable<string> GetSystemFolders(string console)
        {
            if (LibretroSystemMap.TryGetValue(console, out string? primary))
                yield return primary;
            if (SystemFolderFallbacks.TryGetValue(console, out string[]? extras))
                foreach (string f in extras)
                    yield return f;
        }

        public ArtworkService()
        {
            string exeFolder = AppDomain.CurrentDomain.BaseDirectory;
            _vgdbPath = Path.Combine(exeFolder, "Assets", "openvgdb.sqlite");

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _cacheFolder = Path.Combine(appData, "Emutastic", "Artwork");
            Directory.CreateDirectory(_cacheFolder);
            // Build a hash→path index once so the repair pass is O(1) per game.
            _cacheIndex = Directory.EnumerateFiles(_cacheFolder)
                .ToDictionary(
                    f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant(),
                    f => f,
                    StringComparer.OrdinalIgnoreCase);

            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", "Emutastic/1.0");
            _http.Timeout = TimeSpan.FromSeconds(5);

            System.Diagnostics.Debug.WriteLine(
                File.Exists(_vgdbPath)
                    ? $"OpenVGDB found at: {_vgdbPath}"
                    : $"OpenVGDB NOT FOUND at: {_vgdbPath}");
        }

        public async Task<ArtworkResult?> LookupByHashAsync(string md5Hash)
        {
            if (!File.Exists(_vgdbPath)) return null;

            try
            {
                using var connection = new SqliteConnection(
                    $"Data Source={_vgdbPath};Mode=ReadOnly");
                connection.Open();

                var romCmd = connection.CreateCommand();
                romCmd.CommandText = @"
                    SELECT romID, romExtensionlessFileName
                    FROM ROMs
                    WHERE romHashMD5 = $hash
                    LIMIT 1;";
                romCmd.Parameters.AddWithValue("$hash", md5Hash.ToUpperInvariant());

                int romId = -1;
                string title = "";

                using (var reader = romCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        romId = reader.GetInt32(0);
                        title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        System.Diagnostics.Debug.WriteLine(
                            $"OpenVGDB: hash match romID={romId} title={title}");
                    }
                }

                if (romId == -1)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"OpenVGDB: no match for hash {md5Hash}");
                    return null;
                }

                return await GetReleaseByRomIdAsync(connection, romId, title);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"OpenVGDB hash lookup failed: {ex.Message}");
                return null;
            }
        }

        public async Task<ArtworkResult?> LookupByFilenameAsync(string romPath)
        {
            if (!File.Exists(_vgdbPath)) return null;

            try
            {
                string fileName = Path.GetFileNameWithoutExtension(romPath);
                string cleaned = System.Text.RegularExpressions.Regex.Replace(
                    fileName, @"\(.*?\)|\[.*?\]", "").Trim();

                System.Diagnostics.Debug.WriteLine(
                    $"OpenVGDB: trying filename lookup for '{cleaned}'");

                using var connection = new SqliteConnection(
                    $"Data Source={_vgdbPath};Mode=ReadOnly");
                connection.Open();

                int romId = -1;
                string title = "";

                // Exact match first
                var exactCmd = connection.CreateCommand();
                exactCmd.CommandText = @"
                    SELECT romID, romExtensionlessFileName
                    FROM ROMs
                    WHERE romExtensionlessFileName = $name
                    LIMIT 1;";
                exactCmd.Parameters.AddWithValue("$name", fileName);

                using (var reader = exactCmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        romId = reader.GetInt32(0);
                        title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        System.Diagnostics.Debug.WriteLine(
                            $"OpenVGDB: exact filename match romID={romId}");
                    }
                }

                // LIKE match with cleaned name
                if (romId == -1)
                {
                    var likeCmd = connection.CreateCommand();
                    likeCmd.CommandText = @"
                        SELECT romID, romExtensionlessFileName
                        FROM ROMs
                        WHERE romExtensionlessFileName LIKE $name
                        LIMIT 1;";
                    likeCmd.Parameters.AddWithValue("$name", $"%{cleaned}%");

                    using var likeReader = likeCmd.ExecuteReader();
                    if (likeReader.Read())
                    {
                        romId = likeReader.GetInt32(0);
                        title = likeReader.IsDBNull(1) ? "" : likeReader.GetString(1);
                        System.Diagnostics.Debug.WriteLine(
                            $"OpenVGDB: LIKE match romID={romId} title={title}");
                    }
                }

                if (romId == -1)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"OpenVGDB: no filename match for '{fileName}'");
                    return null;
                }

                return await GetReleaseByRomIdAsync(connection, romId, title);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"OpenVGDB filename lookup failed: {ex.Message}");
                return null;
            }
        }

        private async Task<ArtworkResult?> GetReleaseByRomIdAsync(
            SqliteConnection connection, int romId, string fallbackTitle)
        {
            var releaseCmd = connection.CreateCommand();
            releaseCmd.CommandText = @"
                SELECT releaseTitleName,
                       releaseDeveloper,
                       releasePublisher,
                       releaseDate,
                       releaseGenre,
                       releaseDescription
                FROM RELEASES
                WHERE romID = $romId
                LIMIT 1;";
            releaseCmd.Parameters.AddWithValue("$romId", romId);

            using var releaseReader = releaseCmd.ExecuteReader();
            if (!releaseReader.Read())
            {
                System.Diagnostics.Debug.WriteLine(
                    $"OpenVGDB: no release for romID={romId}");
                await Task.CompletedTask;
                return new ArtworkResult { Title = fallbackTitle };
            }

            var result = new ArtworkResult
            {
                Title = releaseReader.IsDBNull(0) ? fallbackTitle : releaseReader.GetString(0),
                Developer = releaseReader.IsDBNull(1) ? "" : releaseReader.GetString(1),
                Publisher = releaseReader.IsDBNull(2) ? "" : releaseReader.GetString(2),
                ReleaseDate = releaseReader.IsDBNull(3) ? "" : releaseReader.GetString(3),
                Genre = releaseReader.IsDBNull(4) ? "" : releaseReader.GetString(4),
                Description = releaseReader.IsDBNull(5) ? "" : releaseReader.GetString(5),
            };

            System.Diagnostics.Debug.WriteLine(
                $"OpenVGDB: release found title={result.Title}");

            await Task.CompletedTask;
            return result;
        }

        private string SanitizeLibretroTitle(string title)
        {
            return title
                .Replace("&", "_")
                .Replace("*", "_")
                .Replace("/", "_")
                .Replace(":", "_")
                .Replace("`", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("?", "_")
                .Replace("\\", "_")
                .Replace("|", "_")
                .Trim();
        }

        private static readonly string[] ThumbnailCategories =
            ["Named_Boxarts", "Named_Titles", "Named_Snaps"];

        // Known title mismatches between common ROM naming and libretro thumbnail naming.
        // Key = normalized user-facing name (lowercase), Value = libretro base title to search instead.
        private static readonly Dictionary<string, string> TitleAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "neo twenty one", "Neo 21" },
            { "neo 21",         "Neo 21" },
        };

        /// <summary>
        /// Fetches the directory listing for a libretro thumbnail folder and caches it
        /// for the session. Returns decoded filenames without extensions.
        /// </summary>
        private async Task<List<string>> GetThumbnailIndexAsync(string systemFolder, string category)
        {
            string key = $"{systemFolder}/{category}";
            if (_thumbnailIndex.TryGetValue(key, out var cached))
                return cached;

            string url = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(systemFolder)}/{category}/";
            try
            {
                string html = await _http.GetStringAsync(url);
                var names = new List<string>();
                var rx = new System.Text.RegularExpressions.Regex(@"href=""([^""]+\.png)""");
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(html))
                {
                    string decoded = Uri.UnescapeDataString(m.Groups[1].Value);
                    names.Add(Path.GetFileNameWithoutExtension(decoded));
                }
                _thumbnailIndex[key] = names;
                return names;
            }
            catch
            {
                _thumbnailIndex[key] = new List<string>();
                return _thumbnailIndex[key];
            }
        }

        /// <summary>
        /// If the title matches a known alias, inserts the canonical libretro name
        /// at the front of the candidate list so it's tried first.
        /// </summary>
        private static void InjectAliases(List<string> candidates)
        {
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                string cleaned = System.Text.RegularExpressions.Regex.Replace(
                    candidates[i].ToLowerInvariant(), @"\s*\(.*", "").Trim();
                if (TitleAliases.TryGetValue(cleaned, out string? alias) &&
                    !candidates.Contains(alias, StringComparer.OrdinalIgnoreCase))
                    candidates.Insert(0, alias);
            }
        }

        /// <summary>
        /// Normalizes a title for fuzzy comparison: lowercase, collapse whitespace,
        /// strip trailing punctuation variants (vs. → vs, etc.).
        /// </summary>
        private static string NormalizeForFuzzy(string s) =>
            System.Text.RegularExpressions.Regex.Replace(
                s.ToLowerInvariant().Trim(), @"\.\s*", " ")   // "vs." → "vs "
            .Replace("  ", " ").Trim();

        /// <summary>
        /// Finds the best libretro thumbnail filename for a given title.
        /// Tries case-insensitive exact match, then punctuation-normalized prefix match
        /// (handles subtitle differences and "vs" vs "vs." discrepancies).
        /// </summary>
        private static string? FindBestThumbnailTitle(string title, List<string> index)
        {
            if (index.Count == 0 || string.IsNullOrWhiteSpace(title)) return null;

            // Exact match (case-insensitive)
            string? exact = index.FirstOrDefault(n => n.Equals(title, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            if (title.Length < 6) return null;

            // Normalized prefix match — strips punctuation differences so
            // "SNK vs Capcom" matches "SNK vs. Capcom - The Match of the Millennium (...)"
            string normTitle = NormalizeForFuzzy(title);
            string? prefix = index.FirstOrDefault(n =>
                NormalizeForFuzzy(n).StartsWith(normTitle));
            return prefix;
        }

        public List<string> BuildLibretroUrlVariants(string console, string gameTitle,
            string category = "Named_Boxarts")
        {
            var urls = new List<string>();
            if (!LibretroSystemMap.TryGetValue(console, out string? systemFolder))
                return urls;
            return BuildLibretroUrlVariantsForFolder(systemFolder, gameTitle, category);
        }

        private List<string> BuildLibretroUrlVariantsForFolder(string systemFolder, string gameTitle,
            string category)
        {
            var urls = new List<string>();
            string encodedSystem = Uri.EscapeDataString(systemFolder);
            string baseUrl = $"https://thumbnails.libretro.com/{encodedSystem}/{category}/";
            string sanitized = SanitizeLibretroTitle(gameTitle);

            var variants = new[]
            {
                sanitized,
                $"{sanitized} (USA)",
                $"{sanitized} (World)",
                $"{sanitized} (Japan)",
                $"{sanitized} (Europe)",
                $"{sanitized} (USA, Europe)",
                $"{sanitized} (Japan, USA)",
                $"{sanitized} (World) (En,Ja)",
                $"{sanitized} (Japan, Europe) (En,Ja)",
                $"{sanitized} (Japan) (En,Ja)",
                $"{sanitized} (En,Ja)",
                $"{sanitized} (USA, Europe) (En,Ja)",
            };

            foreach (string v in variants)
                urls.Add($"{baseUrl}{Uri.EscapeDataString(v)}.png");

            return urls;
        }

        public async Task<string?> DownloadArtworkAsync(string imageUrl, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;

            // Guard against empty hash being used as a cache key — all hashless games
            // would collide on the same file and get each other's artwork.
            if (string.IsNullOrWhiteSpace(cacheKey))
                cacheKey = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(imageUrl)));

            try
            {
                string ext = Path.GetExtension(imageUrl);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
                string localPath = Path.Combine(_cacheFolder, $"{cacheKey}{ext}");

                if (File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"Artwork cache hit: {localPath}");
                    return localPath;
                }

                System.Diagnostics.Debug.WriteLine($"Downloading artwork: {imageUrl}");
                var response = await _http.GetAsync(imageUrl);

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Artwork download failed: {response.StatusCode}");
                    return null;
                }

                byte[] imageBytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(localPath, imageBytes);
                System.Diagnostics.Debug.WriteLine($"Artwork saved: {localPath}");

                return localPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Artwork download failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches a landscape-friendly image for the game detail header.
        /// Tries Named_Snaps first (gameplay screenshots), then Named_Titles (title screens).
        /// Box art is intentionally skipped — it's portrait and doesn't fit the header area.
        /// </summary>
        public async Task<string?> FetchSnapAsync(string romHash, string? romPath, string console)
        {
            var titleCandidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(romPath))
            {
                // For Arcade, Libretro snaps use full titles (not short ROM names), so resolve first.
                if (console == "Arcade")
                {
                    string romName = Path.GetFileNameWithoutExtension(romPath);
                    string? arcadeTitle = _datMatcher.LookupArcadeTitle(romName);
                    if (!string.IsNullOrWhiteSpace(arcadeTitle))
                        titleCandidates.Add(arcadeTitle);
                }

                titleCandidates.Add(Path.GetFileNameWithoutExtension(romPath));
                string cleaned = RomService.CleanTitle(Path.GetFileName(romPath));
                if (!titleCandidates.Contains(cleaned))
                    titleCandidates.Add(cleaned);
            }

            if (titleCandidates.Count == 0) return null;
            InjectAliases(titleCandidates);

            foreach (string category in new[] { "Named_Snaps", "Named_Titles" })
            {
                foreach (string systemFolder in GetSystemFolders(console))
                {
                    // Pass 1: exact URL variants
                    foreach (string title in titleCandidates)
                    {
                        var urls = BuildLibretroUrlVariantsForFolder(systemFolder, title, category);
                        foreach (string url in urls)
                        {
                            string urlHash = Convert.ToHexString(
                                System.Security.Cryptography.MD5.HashData(
                                    System.Text.Encoding.UTF8.GetBytes(url)));
                            string cacheKey = string.IsNullOrWhiteSpace(romHash)
                                ? urlHash
                                : $"{romHash}_{urlHash[..8]}";
                            string? path = await DownloadArtworkAsync(url, cacheKey);
                            if (path != null) return path;
                        }
                    }

                    // Pass 2: fuzzy match via directory listing (handles subtitle mismatches)
                    var index = await GetThumbnailIndexAsync(systemFolder, category);
                    foreach (string title in titleCandidates)
                    {
                        string? matched = FindBestThumbnailTitle(title, index);
                        if (matched == null) continue;
                        string matchUrl = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(systemFolder)}/{category}/{Uri.EscapeDataString(matched)}.png";
                        string matchHash = Convert.ToHexString(
                            System.Security.Cryptography.MD5.HashData(
                                System.Text.Encoding.UTF8.GetBytes(matchUrl)));
                        string matchKey = string.IsNullOrWhiteSpace(romHash) ? matchHash : $"{romHash}_{matchHash[..8]}";
                        string? fuzzyPath = await DownloadArtworkAsync(matchUrl, matchKey);
                        if (fuzzyPath != null) return fuzzyPath;
                    }
                }
            }

            return null;
        }

        public async Task<(string? artworkPath, ArtworkResult? metadata)> FetchArtworkAsync(
            string md5Hash, string? romPath = null, string? console = null)
        {
            // Step 1 — OpenVGDB hash lookup
            var result = await LookupByHashAsync(md5Hash);

            // Step 2 — filename fallback
            if (result == null && !string.IsNullOrWhiteSpace(romPath))
                result = await LookupByFilenameAsync(romPath);

            // Step 3 — use cleaned filename as last resort
            if (result == null && !string.IsNullOrWhiteSpace(romPath))
                result = new ArtworkResult
                {
                    Title = RomService.CleanTitle(Path.GetFileName(romPath))
                };

            if (result == null) return (null, null);

            // Step 4 — try libretro thumbnail variants
            string? artworkPath = null;

            if (!string.IsNullOrWhiteSpace(console))
            {
                // Build list of title candidates to try
                var titleCandidates = new List<string>();

                // For Arcade, Libretro requires the full game title (not short ROM name).
                // Resolve via FBNeo DAT first so we don't waste requests on guaranteed 404s.
                if (console == "Arcade" && !string.IsNullOrWhiteSpace(romPath))
                {
                    string romName = Path.GetFileNameWithoutExtension(romPath);
                    string? arcadeTitle = _datMatcher.LookupArcadeTitle(romName);
                    if (!string.IsNullOrWhiteSpace(arcadeTitle))
                        titleCandidates.Add(arcadeTitle);
                }

                titleCandidates.Add(result.Title);

                if (!string.IsNullOrWhiteSpace(romPath))
                {
                    string raw = RomService.CleanTitle(Path.GetFileName(romPath));
                    if (!titleCandidates.Contains(raw))
                        titleCandidates.Add(raw);

                    string rawNoClean = Path.GetFileNameWithoutExtension(romPath);
                    if (!titleCandidates.Contains(rawNoClean))
                        titleCandidates.Add(rawNoClean);
                }

                InjectAliases(titleCandidates);

                foreach (string category in ThumbnailCategories)
                {
                    if (artworkPath != null) break;

                    foreach (string systemFolder in GetSystemFolders(console))
                    {
                        if (artworkPath != null) break;

                        // Pass 1: exact URL variants
                        foreach (string titleCandidate in titleCandidates)
                        {
                            if (artworkPath != null) break;

                            var urls = BuildLibretroUrlVariantsForFolder(systemFolder, titleCandidate, category);
                            foreach (string url in urls)
                            {
                                artworkPath = await DownloadArtworkAsync(url, md5Hash);
                                if (artworkPath != null)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Artwork found ({category}): {url}");
                                    break;
                                }
                            }
                        }

                        if (artworkPath != null) break;

                        // Pass 2: fuzzy match via directory listing (handles subtitle mismatches)
                        var index = await GetThumbnailIndexAsync(systemFolder, category);
                        foreach (string titleCandidate in titleCandidates)
                        {
                            string? matched = FindBestThumbnailTitle(titleCandidate, index);
                            if (matched == null) continue;
                            string matchUrl = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(systemFolder)}/{category}/{Uri.EscapeDataString(matched)}.png";
                            artworkPath = await DownloadArtworkAsync(matchUrl, md5Hash);
                            if (artworkPath != null)
                            {
                                System.Diagnostics.Debug.WriteLine($"Artwork found fuzzy ({category}): {matchUrl}");
                                break;
                            }
                        }
                    }
                }
            }

            return (artworkPath, result);
        }
    }
}