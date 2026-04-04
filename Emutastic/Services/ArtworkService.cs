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
            { "PS1",          "Sony - PlayStation"                             },
            { "PSP",          "Sony - PlayStation Portable"                    },
            { "TG16",         "NEC - PC Engine - TurboGrafx 16"               },
            { "TGCD",         "NEC - PC Engine CD - TurboGrafx-CD"            },
            { "NGP",          "SNK - Neo Geo Pocket"                           },
            { "Atari2600",    "Atari - 2600"                                   },
            { "Atari5200",    "Atari - 5200"                                   },
            { "Atari7800",    "Atari - 7800"                                   },
            { "Jaguar",       "Atari - Jaguar"                                 },
            { "ColecoVision", "Coleco - ColecoVision"                          },
            { "Intellivision","Mattel - Intellivision"                         },
            { "Vectrex",      "GCE - Vectrex"                                  },
            { "3DO",          "The 3DO Company - 3DO"                          },
        };

        public ArtworkService()
        {
            string exeFolder = AppDomain.CurrentDomain.BaseDirectory;
            _vgdbPath = Path.Combine(exeFolder, "Assets", "openvgdb.sqlite");

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _cacheFolder = Path.Combine(appData, "OpenEmuWindows", "Artwork");
            Directory.CreateDirectory(_cacheFolder);

            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", "OpenEmuWindows/1.0");
            _http.Timeout = TimeSpan.FromSeconds(15);

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

        public List<string> BuildLibretroUrlVariants(string console, string gameTitle,
            string category = "Named_Boxarts")
        {
            var urls = new List<string>();
            if (!LibretroSystemMap.TryGetValue(console, out string? systemFolder))
                return urls;

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
            };

            foreach (string v in variants)
                urls.Add($"{baseUrl}{Uri.EscapeDataString(v)}.png");

            return urls;
        }

        public async Task<string?> DownloadArtworkAsync(string imageUrl, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;

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
                var titleCandidates = new List<string> { result.Title };

                if (!string.IsNullOrWhiteSpace(romPath))
                {
                    string raw = RomService.CleanTitle(Path.GetFileName(romPath));
                    if (raw != result.Title)
                        titleCandidates.Add(raw);

                    string rawNoClean = Path.GetFileNameWithoutExtension(romPath);
                    if (rawNoClean != raw && rawNoClean != result.Title)
                        titleCandidates.Add(rawNoClean);
                }

                foreach (string category in ThumbnailCategories)
                {
                    if (artworkPath != null) break;

                    foreach (string titleCandidate in titleCandidates)
                    {
                        if (artworkPath != null) break;

                        var urls = BuildLibretroUrlVariants(console, titleCandidate, category);
                        foreach (string url in urls)
                        {
                            artworkPath = await DownloadArtworkAsync(url, md5Hash);
                            if (artworkPath != null)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"Artwork found ({category}): {url}");
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