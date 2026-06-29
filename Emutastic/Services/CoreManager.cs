using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    public class CoreManager
    {
        private readonly string _coresFolder;
        private readonly IConfigurationService? _configService;

        // Map console tags to core dll names — in priority order
        public static readonly Dictionary<string, string[]> ConsoleCoreMap = new()
        {
            { "NES",         new[] { "nestopia_libretro.dll",
                                     "quicknes_libretro.dll",
                                     "fceumm_libretro.dll"            }},
            { "FDS",         new[] { "nestopia_libretro.dll"            }},
            { "SNES",        new[] { "snes9x_libretro.dll",
                                     "bsnes_libretro.dll"               }},
            { "N64",         new[] { "parallel_n64_libretro.dll",
                                     "mupen64plus_next_libretro.dll"        }},
            { "GameCube",    new[] { "dolphin_libretro.dll"             }},
            { "GB",          new[] { "mgba_libretro.dll",
                                     "gambatte_libretro.dll",
                                     "sameboy_libretro.dll"             }},
            { "GBC",         new[] { "mgba_libretro.dll",
                                     "gambatte_libretro.dll",
                                     "sameboy_libretro.dll"             }},
            { "GBA",         new[] { "mgba_libretro.dll"               }},
            { "NDS",         new[] { "desmume_libretro.dll",
                                     "melonds_libretro.dll"             }},
            { "3DS",         new[] { "azahar_libretro.dll"              }},
            { "VirtualBoy",  new[] { "mednafen_vb_libretro.dll"         }},
            { "Genesis",     new[] { "genesis_plus_gx_libretro.dll",
                                     "picodrive_libretro.dll"           }},
            { "SegaCD",      new[] { "genesis_plus_gx_libretro.dll"    }},
            { "Sega32X",     new[] { "picodrive_libretro.dll"           }},
            // Default Saturn core is Beetle (mednafen_saturn) — its save states
            // round-trip across launches, unlike Kronos which sets the libretro
            // SINGLE_SESSION quirk and silently invalidates states on app exit.
            // Kronos kept as an alternative for users who need its OpenGL HW
            // upscaling or its Panzer Dragoon Saga / VF3tb fixes.
            { "Saturn",      new[] { "mednafen_saturn_libretro.dll",
                                     "kronos_libretro.dll",
                                     "yabause_libretro.dll"             }},
            { "SMS",         new[] { "genesis_plus_gx_libretro.dll",
                                     "picodrive_libretro.dll"           }},
            { "GameGear",    new[] { "genesis_plus_gx_libretro.dll"    }},
            { "SG1000",      new[] { "genesis_plus_gx_libretro.dll"    }},
            { "Dreamcast",   new[] { "flycast_libretro.dll"             }},
            // Default PS1 core is Beetle PSX HW — the hardware-accelerated
            // sibling of mednafen_psx that supports internal-resolution
            // upscaling, PGXP, and texture filtering via OpenGL. The SW
            // Mednafen PSX is kept as fallback for users who prefer pixel-
            // accurate native rendering.
            { "PS1",         new[] { "mednafen_psx_hw_libretro.dll",
                                     "mednafen_psx_libretro.dll"        }},
            { "PS2",         new[] { "pcsx2_libretro.dll"              }},
            { "PSP",         new[] { "ppsspp_libretro.dll"             }},
            { "TG16",        new[] { "mednafen_pce_libretro.dll",
                                     "mednafen_pce_fast_libretro.dll"        }},
            { "TGCD",        new[] { "mednafen_pce_libretro.dll",
                                     "mednafen_pce_fast_libretro.dll"        }},
            { "NGP",         new[] { "mednafen_ngp_libretro.dll"        }},
            { "Atari2600",   new[] { "stella_libretro.dll"              }},

            { "Atari7800",   new[] { "prosystem_libretro.dll"           }},
            { "Jaguar",      new[] { "virtualjaguar_libretro.dll"       }},
            { "ColecoVision",new[] { "gearcoleco_libretro.dll",
                                     "bluemsx_libretro.dll"             }},

            { "Vectrex",     new[] { "vecx_libretro.dll"                }},
            { "3DO",         new[] { "opera_libretro.dll"               }},
            { "CDi",         new[] { "same_cdi_libretro.dll"            }},
            { "GameCom",     new[] { "tigerbyte_libretro.dll"           }},
            { "NeoGeo",      new[] { "geolith_libretro.dll"              }},
            { "NeoCD",       new[] { "geolith_libretro.dll"              }},
            { "Arcade",      new[] { "fbneo_libretro.dll",
                                     "mame2003_plus_libretro.dll"        }},
        };

        // Region-specific BIOS requirements for consoles where the BIOS must match the game region.
        // Key: console tag → region → candidate filenames (any one is sufficient).
        // "World" is handled by accepting any region's BIOS.
        public static readonly Dictionary<string, Dictionary<string, string[]>> RegionBiosMap = new()
        {
            { "SegaCD", new()
            {
                { "Japan",  new[] { "bios_CD_J.bin" } },
                { "USA",    new[] { "bios_CD_U.bin" } },
                { "Europe", new[] { "bios_CD_E.bin" } },
            }},
            // Beetle Saturn / Kronos BIOS filenames (per libretro docs):
            //   sega_101.bin  — Japan v1.00   MD5: 85ec9ca47d8f6807718151cbcca8b964
            //   mpr-17933.bin — Japan v1.01   MD5: 3240872c70984b6cbfda1586cab68dbe
            //   mpr-17941.bin — USA/EU v1.01  MD5: 4df44ac9af0e58fc63b0e2af9cec25a9
            // NOTE: mpr-17933 is Japan, NOT USA/EU — some community guides have this reversed.
            { "Saturn", new()
            {
                // Beetle Saturn filenames; Kronos uses kronos/saturn_bios.bin (accepted for any region)
                { "Japan",  new[] { "sega_101.bin", "mpr-17933.bin", "kronos/saturn_bios.bin" } },
                { "USA",    new[] { "mpr-17941.bin", "kronos/saturn_bios.bin"                  } },
                { "Europe", new[] { "mpr-17941.bin", "kronos/saturn_bios.bin"                  } },
            }},
            { "PS1", new()
            {
                { "Japan",  new[] { "scph5500.bin"                               } },
                { "USA",    new[] { "scph5501.bin", "scph1001.bin", "scph7001.bin" } },
                { "Europe", new[] { "scph5502.bin"                               } },
            }},
        };

        // Flat fallback list — used when region is unknown or console has no region map.
        // Semantics: any ONE file present = satisfied (regional variants).
        // FDS, TGCD, 3DO: region doesn't affect which BIOS is needed.
        public static readonly Dictionary<string, string[]> ConsoleBiosMap = new()
        {
            { "FDS",      new[] { "disksys.rom"                                           }},
            { "SegaCD",   new[] { "bios_CD_U.bin", "bios_CD_E.bin", "bios_CD_J.bin"      }},
            { "Saturn",   new[] { "sega_101.bin", "mpr-17933.bin", "mpr-17941.bin",
                                  "kronos/saturn_bios.bin"                               }},
            { "PS1",      new[] { "scph5500.bin", "scph5501.bin", "scph5502.bin",
                                  "scph1001.bin", "scph7001.bin"                         }},
            { "3DO",      new[] { "panafz10.bin", "panafz1j.bin", "goldstar.bin"          }},
            { "TGCD",     new[] { "syscard3.pce", "syscard2.pce", "syscard1.pce"          }},
        };

        // Consoles that require ALL listed files (not just any one).
        public static readonly Dictionary<string, string[]> ConsoleBiosRequireAll = new()
        {
            { "NeoGeo",   new[] { "neogeo.zip", "aes.zip" } },
            // Geolith CD mode loads its BIOS from a single archive named
            // neocdz.zip (MAME-style "neocdz" romset) that contains
            // neocd.bin + 000-lo.lo + the other CDZ BIOS files. The cart
            // BIOS is reused too — Geolith expects neogeo.zip + aes.zip
            // for the AES/MVS layer regardless of mode.
            { "NeoCD",    new[] { "neogeo.zip", "aes.zip", "neocdz.zip" } },
            // Game.com boots from both system ROMs; games call into the external kernel.
            { "GameCom",  new[] { "internal.bin", "external.bin" } },
        };

        // Core-specific BIOS requirements — keyed by substring of the core DLL name.
        // Checked only when the resolved core matches. ALL files must be present.
        public static readonly (string CoreMatch, string[] Files)[] CoreBiosMap =
        {
            ("geolith", new[] { "neogeo.zip", "aes.zip" }),
        };

        /// <summary>
        /// Returns the BIOS filenames that are missing for the given console and region.
        /// Checks systemDir first, then any extraDirs (e.g. the ROM file's folder).
        /// When region is detected and a region-specific map exists, only that region's files are checked.
        /// Falls back to the flat ConsoleBiosMap when region is "Unknown" or unmapped.
        /// Returns an empty list when all required files are present or the console needs no BIOS.
        /// </summary>
        public static List<string> GetMissingBios(string console, string systemDir,
            string region = "Unknown", IEnumerable<string>? extraDirs = null,
            string? corePath = null)
        {
            // Expand each caller-supplied dir to include its immediate subdirectories
            // (e.g. a user dropping a BIOS file into "Roms\PS1\BIOS\" alongside their
            // PS1 ROMs in "Roms\PS1\"). Keeps the launch-time check in sync with the
            // Preferences → System Files panel, which does the same shallow recurse.
            static IEnumerable<string> ExpandShallow(string dir)
            {
                yield return dir;
                IEnumerable<string>? subs = null;
                try { subs = Directory.EnumerateDirectories(dir); }
                catch { /* unreadable — skip */ }
                if (subs != null)
                    foreach (var s in subs) yield return s;
            }

            var searchDirs = new[] { systemDir }
                .Concat((extraDirs ?? Enumerable.Empty<string>())
                    .Where(d => !string.IsNullOrEmpty(d))
                    .SelectMany(ExpandShallow))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool FileFound(string filename) =>
                searchDirs.Any(dir => File.Exists(Path.Combine(dir, filename)));

            // Core-specific BIOS requirements (e.g. geolith needs neogeo.zip).
            if (corePath != null)
            {
                string coreName = Path.GetFileName(corePath).ToLowerInvariant();
                foreach (var (coreMatch, files) in CoreBiosMap)
                {
                    if (coreName.Contains(coreMatch))
                        return files.Where(f => !FileFound(f)).ToList();
                }
            }

            // PlayStation 2: LRPS2 reads any valid 4 MB dump from <dir>/pcsx2/bios/,
            // so the gate is satisfied by the presence of any such file rather than
            // a fixed filename. Checks the system dir and any extra (ROM) dirs.
            if (console.Equals("PS2", StringComparison.OrdinalIgnoreCase))
            {
                bool anyPs2Bios = searchDirs.Any(dir =>
                {
                    string biosDir = Path.Combine(dir, "pcsx2", "bios");
                    try
                    {
                        return Directory.Exists(biosDir) &&
                               Directory.EnumerateFiles(biosDir, "*.bin").Any(f =>
                               {
                                   try { return new FileInfo(f).Length >= 4 * 1024 * 1024; }
                                   catch { return false; }
                               });
                    }
                    catch { return false; }
                });
                return anyPs2Bios ? new List<string>()
                                  : new List<string> { @"a PS2 BIOS dump in pcsx2\bios\" };
            }

            // Region-aware path: check only the files needed for this region.
            if (region != "Unknown" && RegionBiosMap.TryGetValue(console, out var regionMap))
            {
                string[] candidates = region == "World"
                    ? regionMap.Values.SelectMany(v => v).Distinct().ToArray()
                    : regionMap.TryGetValue(region, out var rc) ? rc : Array.Empty<string>();

                if (candidates.Length > 0)
                {
                    bool anyPresent = candidates.Any(FileFound);
                    return anyPresent ? new List<string>() : new List<string>(candidates);
                }
            }

            // Require-all: every listed file must be present.
            if (ConsoleBiosRequireAll.TryGetValue(console, out string[]? required))
                return required.Where(f => !FileFound(f)).ToList();

            // Flat fallback (any ONE present = satisfied).
            if (!ConsoleBiosMap.TryGetValue(console, out string[]? flat))
                return new List<string>();

            return flat.Any(FileFound) ? new List<string>() : new List<string>(flat);
        }

        // Lazily-constructed DAT lookup service for arcade core routing. Shared
        // across calls to keep parsed indexes cached.
        private DatMatchService? _datMatcher;
        private DatMatchService DatMatcher => _datMatcher ??= new DatMatchService();

        public CoreManager()
        {
            // Portable mode: cores live under [DataRoot]/Cores/ so the entire
            // portable experience sits inside PortableData/. Otherwise: [exe]/Cores/.
            _coresFolder = AppPaths.GetCoresFolder();
        }

        public CoreManager(IConfigurationService configService) : this()
        {
            _configService = configService;
        }

        public string? GetCorePath(string console)
        {
            if (!ConsoleCoreMap.TryGetValue(console, out string[]? candidates))
                return null;

            // Check for user preferred core first
            if (_configService != null)
            {
                var preferences = _configService.GetCorePreferences();
                if (preferences.PreferredCores.TryGetValue(console, out string? preferredCore))
                {
                    string preferredPath = Path.Combine(_coresFolder, preferredCore);
                    if (File.Exists(preferredPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"Using preferred core '{preferredCore}' for {console}");
                        return preferredPath;
                    }
                }
            }

            // Fall back to default priority order
            foreach (string dll in candidates)
            {
                string path = Path.Combine(_coresFolder, dll);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>
        /// Resolves the core path for a specific game, applying per-ROM routing
        /// where applicable. For arcade games this consults the FBNeo and MAME
        /// 2003-Plus DATs: a ROM the FBNeo DAT recognises goes to FBNeo (better
        /// controls + saves), one only MAME 2003-Plus recognises goes to MAME
        /// 2003-Plus, unrecognised falls back to <see cref="GetCorePath(string)"/>'s
        /// user-preferred-then-priority-order logic.
        ///
        /// For every other console this is identical to <see cref="GetCorePath(string)"/>.
        /// </summary>
        public string? GetCorePathForGame(Models.Game game)
        {
            if (game == null) return null;

            // 1. Honour per-game PreferredCore (set at import time by ImportService
            //    for consoles with multiple cores, e.g. Arcade FBNeo vs MAME 2003-Plus).
            if (!string.IsNullOrEmpty(game.PreferredCore))
            {
                string preferredPath = Path.Combine(_coresFolder, game.PreferredCore);
                if (File.Exists(preferredPath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CoreManager] Using per-game PreferredCore '{game.PreferredCore}' for '{game.Title}'");
                    return preferredPath;
                }
                // Preferred core not installed — fall through to legacy/default logic.
            }

            // 2. Legacy fallback for games imported before PreferredCore existed:
            //    do a fresh DAT lookup for arcade ROMs so they still get routed
            //    correctly even without a DB update. (One-time cost; new imports
            //    skip this branch via the PreferredCore short-circuit above.)
            if (game.Console.Equals("Arcade", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(game.RomPath))
            {
                string romName = Path.GetFileNameWithoutExtension(game.RomPath);
                string? routedDll = DatMatcher.GetPreferredArcadeCore(romName);
                if (!string.IsNullOrEmpty(routedDll))
                {
                    string routedPath = Path.Combine(_coresFolder, routedDll);
                    if (File.Exists(routedPath))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CoreManager] Legacy DAT-routed arcade: '{romName}' → {routedDll}");
                        return routedPath;
                    }
                }
            }

            // 3. Standard preferred-or-priority resolution.
            return GetCorePath(game.Console);
        }

        public bool HasCore(string console)
            => GetCorePath(console) != null;

        public List<string> GetMissingCores(string console)
        {
            var missing = new List<string>();
            if (!ConsoleCoreMap.TryGetValue(console, out string[]? candidates))
                return missing;

            foreach (string dll in candidates)
            {
                string path = Path.Combine(_coresFolder, dll);
                if (!File.Exists(path))
                    missing.Add(dll);
            }
            return missing;
        }
    }
}