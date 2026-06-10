using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Emutastic.Services
{
    public class RomService
    {
        // Extensions that could belong to multiple systems — require user disambiguation at import time.
        // Ordered by most common system first (shown that way in the picker).
        public static readonly Dictionary<string, string[]> AmbiguousExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".chd", new[] { "PS2", "SegaCD", "Saturn", "PS1", "TGCD", "NeoCD", "3DO", "Dreamcast", "CDi" } },
            { ".iso", new[] { "PS2", "PSP", "GameCube", "NeoCD", "3DO" } },
            { ".cue", new[] { "SegaCD", "Saturn", "PS1", "TGCD", "NeoCD", "3DO", "CDi" } },
            // .m3u playlists are how the libretro disc-control interface sees a
            // multi-disc set as N images. GameCube included so auto-bundled
            // multi-disc GC titles (Resident Evil 0, Baten Kaitos, etc.) get
            // routed correctly when the folder name disambiguates. Ambiguity
            // resolved via DAT lookup, folder name, or user picker.
            { ".m3u", new[] { "PS2", "SegaCD", "Saturn", "PS1", "TGCD", "NeoCD", "3DO", "CDi", "GameCube", "Amiga" } },
            { ".bin", new[] { "PS1", "PS2", "SegaCD", "Saturn", "NeoCD", "3DO", "Dreamcast", "Atari7800", "Atari2600", "Genesis", "Sega32X", "ColecoVision", "NES", "NGP" } },
        };

        // ROM file extensions mapped to console names
        private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".nes",  "NES"         },
            { ".fds",  "FDS"         },
            { ".snes", "SNES"        },
            { ".sfc",  "SNES"        },
            { ".smc",  "SNES"        },
            { ".z64",  "N64"         },
            { ".n64",  "N64"         },
            { ".v64",  "N64"         },
            { ".gcm",  "GameCube"    },
            { ".rvz",  "GameCube"    },
            { ".wbfs", "GameCube"    },
            { ".gcz",  "GameCube"    },
            { ".wia",  "GameCube"    },
            { ".ciso", "GameCube"    },
            { ".gb",   "GB"          },
            { ".gbc",  "GBC"         },
            { ".gba",  "GBA"         },
            { ".nds",  "NDS"         },
            { ".3ds",  "3DS"         },
            { ".cci",  "3DS"         },
            { ".cia",  "3DS"         },
            { ".cxi",  "3DS"         },
            { ".3dsx", "3DS"         },
            { ".app",  "3DS"         },
            { ".vb",   "VirtualBoy"  },
            { ".md",   "Genesis"     },
            { ".gen",  "Genesis"     },
            { ".smd",  "Genesis"     },
            { ".32x",  "Sega32X"     },
            { ".sms",  "SMS"         },
            { ".gg",   "GameGear"    },
            { ".sg",   "SG1000"      },
            { ".psx",  "PS1"         },
            { ".pbp",  "PSP"         },
            { ".cso",  "PSP"         },
            { ".pce",  "TG16"        },
            { ".ngp",  "NGP"         },
            { ".ngc",  "NGP"         },
            { ".a26",  "Atari2600"   },

            { ".a78",  "Atari7800"   },
            { ".j64",  "Jaguar"      },
            { ".col",  "ColecoVision"},

            { ".vec",  "Vectrex"     },
            { ".gdi",  "Dreamcast"   },
            { ".cdi",  "Dreamcast"   },
            { ".neo",  "NeoGeo"      },
            { ".zip",  "Arcade"      },
            { ".7z",   "Arcade"      },
        };

        // Console to manufacturer mapping
        private static readonly Dictionary<string, string> ManufacturerMap = new()
        {
            { "NES",          "Nintendo"   }, { "FDS",       "Nintendo"   },
            { "SNES",         "Nintendo"   }, { "N64",       "Nintendo"   },
            { "GameCube",     "Nintendo"   }, { "GB",        "Nintendo"   },
            { "GBC",          "Nintendo"   }, { "GBA",       "Nintendo"   },
            { "NDS",          "Nintendo"   }, { "3DS",       "Nintendo"   },
            { "VirtualBoy","Nintendo"   },

            { "Genesis",      "Sega"       }, { "SegaCD",    "Sega"       },
            { "Sega32X",      "Sega"       }, { "Saturn",    "Sega"       },
            { "SMS",          "Sega"       }, { "GameGear",  "Sega"       },
            { "SG1000",       "Sega"       }, { "Dreamcast", "Sega"       },
            { "PS1",          "Sony"       },
            { "PS2",          "Sony"       },
            { "PSP",          "Sony"       },
            { "TG16",         "NEC"        }, { "TGCD",      "NEC"        },
            { "NGP",          "SNK"        },
            { "NGPC",         "SNK"        },
            { "NeoGeo",       "SNK"        },
            { "NeoCD",        "SNK"        },
            { "Atari2600",    "Atari"      },
            { "Atari7800",    "Atari"      },
            { "Jaguar",       "Atari"      },
            { "ColecoVision", "Coleco"     },

            { "Vectrex",      "GCE"        },
            { "3DO",          "3DO"        },
            { "CDi",          "Philips"    },
            { "Arcade",       "Arcade"     },
        };

        // Console to background/accent color mapping
        private static readonly Dictionary<string, (string bg, string accent)> ConsoleColors = new()
        {
            { "NES",         ("#1A0A0A", "#C8102E") },
            { "SNES",        ("#1A0A2E", "#7B2FBE") },
            { "N64",         ("#0A1A2E", "#E03535") },
            { "GameCube",    ("#0A1A1A", "#6A0DAD") },
            { "GB",          ("#1A2E1A", "#8BC34A") },
            { "GBC",         ("#1A2E1A", "#FF6B6B") },
            { "GBA",         ("#1A1A2E", "#9C27B0") },
            { "NDS",         ("#0A2E1A", "#4CAF50") },
            { "3DS",         ("#0A0A2E", "#E4002B") },
            { "Genesis",     ("#1A1A0A", "#2196F3") },
            { "Saturn",      ("#2E1A0A", "#FF9800") },
            { "SegaCD",      ("#0A2E2E", "#00BCD4") },
            { "SMS",         ("#0A1A2E", "#3F51B5") },
            { "GameGear",    ("#2E0A1A", "#E91E63") },
            { "PS1",         ("#0A0A2E", "#2196F3") },
            { "PS2",         ("#0A0A1A", "#3D5AFE") },
            { "PSP",         ("#0A1A2E", "#00BCD4") },
            { "Atari2600",   ("#2E1A0A", "#FF5722") },
            { "TG16",        ("#1A2E2E", "#009688") },
            { "Dreamcast",   ("#1A0A0A", "#FF6600") },
            { "CDi",         ("#1A1A2E", "#00897B") },
            { "NGP",         ("#1A0A1A", "#C8A951") },
            { "NGPC",        ("#1A0A1A", "#D4A843") },
            { "NeoGeo",      ("#1A0A1A", "#FFD700") },
            { "NeoCD",       ("#1A0A1A", "#FFB347") },
            { "Arcade",      ("#0A0A0A", "#E03535") },
        };

        public static bool IsRomFile(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            return ExtensionMap.ContainsKey(ext) || AmbiguousExtensions.ContainsKey(ext);
        }

        public static bool IsRomExtension(string ext)
        {
            return ExtensionMap.ContainsKey(ext) || AmbiguousExtensions.ContainsKey(ext);
        }

        /// <summary>Returns null for unambiguous extensions; returns the candidate list for ambiguous ones.</summary>
        public static string[]? GetAmbiguousCandidates(string ext)
            => AmbiguousExtensions.TryGetValue(ext, out string[]? c) ? c : null;

        /// <summary>
        /// All file extensions that could plausibly belong to <paramref name="console"/>.
        /// Includes unambiguous extensions (e.g. .sfc → SNES), ambiguous shared
        /// extensions where this console is one of the candidates (e.g. .cue lists
        /// PS1 / Saturn / SegaCD / etc.), AND archive extensions (.zip / .7z) for
        /// every non-archive-native console — zipped ROMs are the standard
        /// distribution format and the import pipeline classifies them by their
        /// inner contents, not by the .zip extension itself. Used by the
        /// per-console "Refresh Library" action to scope the rescan to candidate
        /// files instead of importing everything in the folder.
        /// </summary>
        public static IEnumerable<string> GetExtensionsForConsole(string console)
        {
            foreach (var kvp in ExtensionMap)
                if (string.Equals(kvp.Value, console, StringComparison.OrdinalIgnoreCase))
                    yield return kvp.Key;
            foreach (var kvp in AmbiguousExtensions)
                if (kvp.Value.Any(c => string.Equals(c, console, StringComparison.OrdinalIgnoreCase)))
                    yield return kvp.Key;
            // Archive formats — refresh-side filter for any console. Importer
            // peeks inside and classifies by inner ROM, then dedupes by hash,
            // so non-{console} archives in the same folder produce zero new
            // {console} games (the count below is per-console-filtered).
            // For Arcade itself, .zip/.7z are already in ExtensionMap, so the
            // dedup of yielded values via the consumer's HashSet absorbs the
            // overlap.
            if (!string.Equals(console, "Arcade", StringComparison.OrdinalIgnoreCase))
            {
                yield return ".zip";
                yield return ".7z";
            }
        }

        public static string DetectConsole(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            return ExtensionMap.TryGetValue(ext, out string? console)
                ? console
                : "Unknown";
        }

        // Keyword → console tag. Checked against each folder segment (case-insensitive).
        private static readonly (string keyword, string console)[] FolderKeywords =
        [
            ("atari 7800",    "Atari7800"),
            ("atari7800",     "Atari7800"),
            ("7800",          "Atari7800"),
            ("atari 2600",    "Atari2600"),
            ("atari2600",     "Atari2600"),
            ("2600",          "Atari2600"),

            ("mega drive",    "Genesis"),
            ("genesis",       "Genesis"),
            ("sega 32x",      "Sega32X"),
            ("32x",           "Sega32X"),
            ("sega cd",       "SegaCD"),
            ("segacd",        "SegaCD"),
            ("mega-cd",       "SegaCD"),
            ("colecovision",  "ColecoVision"),
            ("coleco",        "ColecoVision"),

            ("nintendo entertainment", "NES"),
            (" nes",          "NES"),
            ("famicom",       "NES"),
            ("super nintendo","SNES"),
            ("snes",          "SNES"),
            ("super famicom", "SNES"),
            ("game boy advance","GBA"),
            ("game boy color","GBC"),
            ("game boy",      "GB"),
            ("nintendo 64",   "N64"),
            ("n64",           "N64"),
            ("nintendo ds",   "NDS"),
            ("nintendo 3ds",  "3DS"),
            ("3ds",           "3DS"),
            // CD variants must come before plain TG16/PC Engine to avoid false matches
            ("turbografx-cd", "TGCD"),
            ("turbografx cd", "TGCD"),
            ("turbografx16 cd","TGCD"),
            ("turbografx 16 cd","TGCD"),
            ("tgfx16-cd",     "TGCD"),
            ("tgfx16 cd",     "TGCD"),
            ("tgfx-cd",       "TGCD"),
            ("pc engine cd",  "TGCD"),
            ("pc engine duo", "TGCD"),
            ("pc engine-cd",  "TGCD"),
            ("tgcd",          "TGCD"),
            ("turbografx",    "TG16"),
            ("tgfx",          "TG16"),
            ("pc engine",     "TG16"),
            ("arcade",        "Arcade"),
            ("fbneo",         "Arcade"),
            ("fba",           "Arcade"),
            ("mame",          "Arcade"),
            ("neo geo pocket color","NGPC"),  // must come before "neo geo pocket"
            ("neo geo pocket","NGP"),         // must come before "neo geo"
            // NeoCD aliases must come before the NeoGeo (cart) ones since
            // every "neo geo cd" string contains "neo geo" as a substring.
            ("neo geo cd",    "NeoCD"),
            ("neogeo cd",     "NeoCD"),
            ("neo-geo-cd",    "NeoCD"),
            ("neogeocd",      "NeoCD"),
            ("ngcd",          "NeoCD"),
            ("neo geo",       "NeoGeo"),
            ("neogeo",        "NeoGeo"),
            ("neo-geo",       "NeoGeo"),
            ("lunagarlic",    "NeoGeo"),
            ("cps1",          "Arcade"),
            ("cps2",          "Arcade"),
            ("cps3",          "Arcade"),
            ("capcom",        "Arcade"),
            ("sega saturn",   "Saturn"),
            ("saturn",        "Saturn"),
            ("dreamcast",     "Dreamcast"),
            // PlayStation aliases — must come in most-specific-first order so
            // "playstation portable"/"playstation 2" match PSP/PS2 before the bare
            // "playstation" substring falls through to PS1.
            ("playstation portable", "PSP"),
            ("psp",           "PSP"),
            ("sony playstation 2", "PS2"),
            ("playstation 2", "PS2"),
            ("ps2",           "PS2"),
            ("playstation",   "PS1"),
            ("psx",           "PS1"),
            ("ps1",           "PS1"),
            ("gamecube",      "GameCube"),
            ("game cube",     "GameCube"),
            ("nintendo gamecube", "GameCube"),
            ("master system", "SMS"),
            ("sega master",   "SMS"),
            ("game gear",     "GameGear"),
            ("gamegear",      "GameGear"),
            ("sg-1000",       "SG1000"),
            ("sg1000",        "SG1000"),
            ("virtual boy",   "VirtualBoy"),
            ("virtualboy",    "VirtualBoy"),
            ("jaguar",        "Jaguar"),
            ("vectrex",       "Vectrex"),
            ("3do",           "3DO"),
            ("panasonic",     "3DO"),
            ("3do interactive","3DO"),
            ("philips cd-i",  "CDi"),
            ("philips cdi",   "CDi"),
            ("cd-i",          "CDi"),
            ("cdi",           "CDi"),
        ];

        /// <summary>
        /// Tries to identify the console from folder names in the given path.
        /// Returns empty string if no match is found.
        /// </summary>
        public static string DetectConsoleFromFolderName(string filePath)
        {
            // Walk each directory component and check against keywords.
            string? dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                string folderName = Path.GetFileName(dir) ?? "";
                string lower = folderName.ToLowerInvariant();
                foreach (var (keyword, console) in FolderKeywords)
                {
                    if (lower.Contains(keyword))
                        return console;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return string.Empty;
        }

        /// <summary>
        /// Detects the region from a filename using No-Intro/Redump naming conventions.
        /// Returns "Japan", "USA", "Europe", "World", or "Unknown".
        /// </summary>
        public static string DetectRegion(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            // Match the parenthesised region tag anywhere in the filename
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                    @"\(Japan\)|\(Japan,", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "Japan";
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                    @"\(USA\)|\(USA,|\(U\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "USA";
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                    @"\(Europe\)|\(Europe,|\(E\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "Europe";
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                    @"\(World\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "World";
            return "Unknown";
        }

        public static string DetectManufacturer(string console)
        {
            return ManufacturerMap.TryGetValue(console, out string? manufacturer)
                ? manufacturer
                : "Unknown";
        }

        /// <summary>
        /// Returns true when <paramref name="tag"/> is a canonical console tag known
        /// to the rest of the app (everything in <see cref="ManufacturerMap"/>).
        /// Used to validate user-supplied import hints before they reach detection.
        /// </summary>
        public static bool IsKnownConsoleTag(string? tag)
        {
            return !string.IsNullOrEmpty(tag) && ManufacturerMap.ContainsKey(tag);
        }

        // Box art aspect ratios (width / height) — empirically measured from
        // the user's actual ScreenScraper artwork library, NOT physical box
        // shapes. ScreenScraper often returns landscape promotional artwork
        // even for consoles whose physical boxes are tall (SNES, NES, 3DS,
        // DS), so values trend higher (more landscape) than you'd expect from
        // looking at a physical cart/box.
        // Values below 1.0 = portrait, above 1.0 = landscape.
        // To re-measure if user changes art source or finds another off-by-x,
        // see project_consoleboxratios_screenscraper memory for the PowerShell.
        private static readonly Dictionary<string, double> ConsoleBoxRatios = new()
        {
            { "NES",          0.78 },
            { "FDS",          0.92 },
            { "SNES",         1.41 },
            { "N64",          1.39 },
            { "GameCube",     0.75 },
            { "GB",           1.02 },
            { "GBC",          1.01 },
            { "GBA",          1.04 },
            { "NDS",          1.12 },
            { "3DS",          1.12 },
            { "VirtualBoy",   1.02 },
            { "Genesis",      0.70 },
            { "SegaCD",       0.66 },
            { "Sega32X",      0.83 },
            { "Saturn",       0.77 },
            { "SMS",          0.83 },
            { "GameGear",     0.71 },
            { "SG1000",       0.97 },
            { "Dreamcast",    1.04 },
            { "PS1",          1.00 },
            { "PS2",          0.71 },   // DVD-case art; tune against ScreenScraper box-2D if needed
            { "PSP",          0.98 },
            { "TG16",         0.98 },
            { "TGCD",         1.00 },
            { "NGP",          1.00 },
            { "NGPC",         1.00 },
            { "Atari2600",    0.73 },
            { "Atari7800",    0.96 },
            { "Jaguar",       0.84 },
            { "ColecoVision", 0.73 },
            { "Vectrex",      0.76 },
            { "3DO",          1.09 },
            { "CDi",          1.02 },
            { "NeoGeo",       0.81 },
            { "NeoCD",        1.00 },
            { "Arcade",       0.84 },
        };

        public static double GetBoxRatio(string console)
            => ConsoleBoxRatios.TryGetValue(console, out var r) ? r : 0.73;

        public static (string bg, string accent) GetConsoleColors(string console)
        {
            return ConsoleColors.TryGetValue(console, out var colors)
                ? colors
                : ("#1F1F21", "#E03535");
        }

        // ── Persistent hash cache ────────────────────────────────────────
        // Survives DB rebuilds so reimporting the same ROM files is instant.
        // Key = "path|size|lastWriteUtc"  →  Value = md5 hex string
        private static readonly object _hashCacheLock = new();
        private static Dictionary<string, string>? _hashCache;
        private static readonly string _hashCachePath =
            Path.Combine(AppPaths.DataRoot, "hash_cache.txt");

        private static Dictionary<string, string> LoadHashCache()
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_hashCachePath)) return cache;
            try
            {
                foreach (var line in File.ReadLines(_hashCachePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        cache[line[..eq]] = line[(eq + 1)..];
                }
            }
            catch { /* corrupt file — start fresh */ }
            return cache;
        }

        private static void SaveHashCacheEntry(string key, string hash)
        {
            try { File.AppendAllText(_hashCachePath, $"{key}={hash}\n"); }
            catch { /* non-fatal */ }
        }

        private static string MakeCacheKey(string filePath)
        {
            var fi = new FileInfo(filePath);
            return $"{filePath}|{fi.Length}|{fi.LastWriteTimeUtc:o}";
        }

        // Files above this size use a fast fingerprint (first 1 MB + last 1 MB + size)
        // instead of a full-file MD5. Full MD5 of a multi-GB ISO (PSP, GameCube) takes
        // ~30 s and saturates the disk during bulk imports; the partial fingerprint
        // runs in ~10 ms and still uniquely identifies the file for cache/dedup purposes
        // (filesize is mixed in, so two different-sized files can't collide).
        private const long LargeFileThresholdBytes = 256L * 1024 * 1024;
        private const int  FingerprintWindowBytes  = 1024 * 1024;

        public static string HashRom(string filePath)
        {
            string cacheKey = MakeCacheKey(filePath);

            lock (_hashCacheLock)
            {
                _hashCache ??= LoadHashCache();
                if (_hashCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            string hex;
            long size = new FileInfo(filePath).Length;
            using var md5 = MD5.Create();

            if (size > LargeFileThresholdBytes)
            {
                // Fast fingerprint: head + tail + filesize
                using var stream = File.OpenRead(filePath);
                var head = new byte[FingerprintWindowBytes];
                int headRead = stream.Read(head, 0, head.Length);
                md5.TransformBlock(head, 0, headRead, null, 0);

                stream.Seek(-FingerprintWindowBytes, SeekOrigin.End);
                var tail = new byte[FingerprintWindowBytes];
                int tailRead = stream.Read(tail, 0, tail.Length);
                md5.TransformBlock(tail, 0, tailRead, null, 0);

                var sizeBytes = BitConverter.GetBytes(size);
                md5.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);
                hex = BitConverter.ToString(md5.Hash!).Replace("-", "").ToLowerInvariant();
            }
            else
            {
                using var stream = File.OpenRead(filePath);
                byte[] hash = md5.ComputeHash(stream);
                hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }

            lock (_hashCacheLock)
            {
                _hashCache![cacheKey] = hex;
            }
            SaveHashCacheEntry(cacheKey, hex);

            return hex;
        }

        public static string CleanTitle(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);

            // Remove common ROM tags like (USA), [!], (Rev 1) etc.
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\(.*?\)", "");
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\[.*?\]", "");

            // Clean up extra spaces
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();

            return name;
        }
    }
}