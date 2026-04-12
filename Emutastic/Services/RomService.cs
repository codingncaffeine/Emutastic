using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Emutastic.Services
{
    public class RomService
    {
        // Extensions that could belong to multiple systems — require user disambiguation at import time.
        // Ordered by most common system first (shown that way in the picker).
        public static readonly Dictionary<string, string[]> AmbiguousExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".chd", new[] { "SegaCD", "Saturn", "PS1", "TGCD", "3DO", "Dreamcast", "CDi" } },
            { ".iso", new[] { "PSP", "GameCube", "3DO" } },
            { ".cue", new[] { "SegaCD", "Saturn", "PS1", "TGCD", "3DO", "CDi" } },
            { ".bin", new[] { "PS1", "SegaCD", "Saturn", "3DO", "Dreamcast", "Atari7800", "Atari2600", "Genesis", "Sega32X", "ColecoVision", "NES", "NGP" } },
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
            { "PS1",          "Sony"       }, { "PSP",       "Sony"       },
            { "TG16",         "NEC"        }, { "TGCD",      "NEC"        },
            { "NGP",          "SNK"        },
            { "NeoGeo",       "SNK"        },
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
            { "PSP",         ("#0A1A2E", "#00BCD4") },
            { "Atari2600",   ("#2E1A0A", "#FF5722") },
            { "TG16",        ("#1A2E2E", "#009688") },
            { "Dreamcast",   ("#1A0A0A", "#FF6600") },
            { "CDi",         ("#1A1A2E", "#00897B") },
            { "NeoGeo",      ("#1A0A1A", "#FFD700") },
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
            ("neo geo",       "NeoGeo"),  // must come after "neo geo pocket"
            ("neogeo",        "NeoGeo"),
            ("neo-geo",       "NeoGeo"),
            ("lunagarlic",    "NeoGeo"),
            ("cps1",          "Arcade"),
            ("cps2",          "Arcade"),
            ("cps3",          "Arcade"),
            ("capcom",        "Arcade"),
            ("neo geo pocket","NGP"),
            ("sega saturn",   "Saturn"),
            ("saturn",        "Saturn"),
            ("dreamcast",     "Dreamcast"),
            ("playstation portable", "PSP"),
            ("psp",           "PSP"),
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

        // Box art aspect ratios (width / height) sourced from actual Libretro thumbnail repository.
        // Values below 1.0 = portrait, above 1.0 = landscape.
        private static readonly Dictionary<string, double> ConsoleBoxRatios = new()
        {
            { "NES",          0.64 },  // US clamshell / Famicom vertical box
            { "FDS",          0.64 },  // same box as NES
            { "SNES",         0.68 },  // large Nintendo clamshell
            { "N64",          1.37 },  // famously wide N64 cardboard box (landscape)
            { "GameCube",     0.73 },  // DVD keepcase
            { "GB",           0.81 },  // Game Boy clamshell
            { "GBC",          0.82 },  // same physical box as GB
            { "GBA",          0.98 },  // GBA clamshell (near-square)
            { "NDS",          0.95 },  // DS keepcase
            { "3DS",          0.72 },  // 3DS keepcase
            { "VirtualBoy",   0.90 },  // Virtual Boy box
            { "Genesis",      0.66 },  // Genesis clamshell
            { "SegaCD",       0.66 },  // same Genesis clamshell
            { "Sega32X",      0.66 },  // same Genesis clamshell
            { "Saturn",       0.63 },  // tall custom Sega Saturn jewel case
            { "SMS",          0.73 },  // Master System box
            { "GameGear",     0.73 },  // Game Gear clamshell
            { "SG1000",       1.03 },  // SG-1000 box (near-square)
            { "Dreamcast",    0.93 },  // GD-ROM jewel case
            { "PS1",          0.95 },  // CD jewel case
            { "PSP",          0.70 },  // UMD keepcase
            { "TG16",         0.90 },  // HuCard box
            { "TGCD",         0.90 },  // same as TG16
            { "NGP",          1.28 },  // Neo Geo Pocket hang-tab card (landscape)
            { "Atari2600",    0.83 },  // 2600 box

            { "Atari7800",    0.88 },  // 7800 box
            { "Jaguar",       0.73 },  // Jaguar box
            { "ColecoVision", 0.73 },  // ColecoVision box

            { "Vectrex",      0.75 },  // Vectrex box
            { "3DO",          0.58 },  // exceptionally tall 3DO slipcase
            { "NeoGeo",       0.73 },  // Neo Geo AES box art
            { "Arcade",       0.73 },  // arcade flyer art
        };

        public static double GetBoxRatio(string console)
            => ConsoleBoxRatios.TryGetValue(console, out var r) ? r : 0.73;

        public static (string bg, string accent) GetConsoleColors(string console)
        {
            return ConsoleColors.TryGetValue(console, out var colors)
                ? colors
                : ("#1F1F21", "#E03535");
        }

        public static string HashRom(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
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