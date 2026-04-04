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
                                     "gambatte_libretro.dll"            }},
            { "GBC",         new[] { "mgba_libretro.dll",
                                     "gambatte_libretro.dll"            }},
            { "GBA",         new[] { "mgba_libretro.dll"               }},
            { "NDS",         new[] { "desmume_libretro.dll",
                                     "melonds_libretro.dll"             }},
            { "VirtualBoy",  new[] { "mednafen_vb_libretro.dll"         }},
            { "Genesis",     new[] { "genesis_plus_gx_libretro.dll",
                                     "picodrive_libretro.dll"           }},
            { "SegaCD",      new[] { "genesis_plus_gx_libretro.dll"    }},
            { "Sega32X",     new[] { "picodrive_libretro.dll"           }},
            { "Saturn",      new[] { "kronos_libretro.dll",
                                     "mednafen_saturn_libretro.dll",
                                     "yabause_libretro.dll"             }},
            { "SMS",         new[] { "genesis_plus_gx_libretro.dll",
                                     "picodrive_libretro.dll"           }},
            { "GameGear",    new[] { "genesis_plus_gx_libretro.dll"    }},
            { "SG1000",      new[] { "genesis_plus_gx_libretro.dll"    }},
            { "PS1",         new[] { "mednafen_psx_libretro.dll",
                                     "pcsx_rearmed_libretro.dll"        }},
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
            { "Intellivision",new[]{ "freeintv_libretro.dll"            }},
            { "Vectrex",     new[] { "vecx_libretro.dll"                }},
            { "3DO",         new[] { "opera_libretro.dll"               }},
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
            { "Intellivision", new[] { "exec.bin", "grom.bin" } },
        };

        /// <summary>
        /// Returns the BIOS filenames that are missing for the given console and region.
        /// Checks systemDir first, then any extraDirs (e.g. the ROM file's folder).
        /// When region is detected and a region-specific map exists, only that region's files are checked.
        /// Falls back to the flat ConsoleBiosMap when region is "Unknown" or unmapped.
        /// Returns an empty list when all required files are present or the console needs no BIOS.
        /// </summary>
        public static List<string> GetMissingBios(string console, string systemDir,
            string region = "Unknown", IEnumerable<string>? extraDirs = null)
        {
            var searchDirs = new[] { systemDir }
                .Concat(extraDirs ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrEmpty(d))
                .ToArray();

            bool FileFound(string filename) =>
                searchDirs.Any(dir => File.Exists(Path.Combine(dir, filename)));

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

        public CoreManager()
        {
            string exeFolder = AppDomain.CurrentDomain.BaseDirectory;
            _coresFolder = Path.Combine(exeFolder, "Cores");
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