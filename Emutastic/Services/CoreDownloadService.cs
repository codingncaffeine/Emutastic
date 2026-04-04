using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public class CoreEntry
    {
        public string FileName    { get; init; } = "";   // e.g. "snes9x_libretro.dll"
        public string DisplayName { get; init; } = "";
        public string[] Systems   { get; init; } = [];
        public bool Recommended   { get; init; } = true;
    }

    public enum CoreStatus { NotInstalled, Installed, UpdateAvailable }

    public class CoreDownloadService
    {
        // ── Catalog ───────────────────────────────────────────────────────────
        public static readonly IReadOnlyList<CoreEntry> Catalog = new List<CoreEntry>
        {
            new() { FileName = "nestopia_libretro.dll",         DisplayName = "Nestopia",              Systems = ["NES", "FDS"],                        Recommended = true  },
            new() { FileName = "quicknes_libretro.dll",         DisplayName = "QuickNES",              Systems = ["NES"],                               Recommended = false },
            new() { FileName = "fceumm_libretro.dll",           DisplayName = "FCE Ultra MM",          Systems = ["NES"],                               Recommended = false },
            new() { FileName = "snes9x_libretro.dll",           DisplayName = "Snes9x",                Systems = ["SNES"],                              Recommended = true  },
            new() { FileName = "bsnes_libretro.dll",            DisplayName = "bsnes",                 Systems = ["SNES"],                              Recommended = false },
            new() { FileName = "parallel_n64_libretro.dll",     DisplayName = "Parallel N64",          Systems = ["N64"],                               Recommended = true  },
            new() { FileName = "mupen64plus_next_libretro.dll", DisplayName = "Mupen64Plus-Next",      Systems = ["N64"],                               Recommended = false },
            new() { FileName = "dolphin_libretro.dll",          DisplayName = "Dolphin",               Systems = ["GameCube"],                          Recommended = true  },
            new() { FileName = "mgba_libretro.dll",             DisplayName = "mGBA",                  Systems = ["GB", "GBC", "GBA"],                  Recommended = true  },
            new() { FileName = "gambatte_libretro.dll",         DisplayName = "Gambatte",              Systems = ["GB", "GBC"],                         Recommended = false },
            new() { FileName = "sameboy_libretro.dll",          DisplayName = "SameBoy",               Systems = ["GB", "GBC"],                         Recommended = false },
            new() { FileName = "desmume_libretro.dll",          DisplayName = "DeSmuME",               Systems = ["NDS"],                               Recommended = true  },
            new() { FileName = "melonds_libretro.dll",          DisplayName = "melonDS",               Systems = ["NDS"],                               Recommended = false },
            new() { FileName = "mednafen_vb_libretro.dll",      DisplayName = "Mednafen Virtual Boy",  Systems = ["VirtualBoy"],                        Recommended = true  },
            new() { FileName = "genesis_plus_gx_libretro.dll",  DisplayName = "Genesis Plus GX",       Systems = ["Genesis", "SegaCD", "SMS", "GameGear", "SG1000"], Recommended = true  },
            new() { FileName = "picodrive_libretro.dll",        DisplayName = "PicoDrive",             Systems = ["Genesis", "Sega32X", "SMS"],         Recommended = false },
            new() { FileName = "kronos_libretro.dll",           DisplayName = "Kronos",                Systems = ["Saturn"],                            Recommended = true  },
            new() { FileName = "mednafen_saturn_libretro.dll",  DisplayName = "Mednafen Saturn",       Systems = ["Saturn"],                            Recommended = false },
            new() { FileName = "yabause_libretro.dll",          DisplayName = "Yabause",               Systems = ["Saturn"],                            Recommended = false },
            new() { FileName = "mednafen_psx_libretro.dll",     DisplayName = "Mednafen PSX (Beetle)", Systems = ["PS1"],                               Recommended = true  },
            new() { FileName = "pcsx_rearmed_libretro.dll",     DisplayName = "PCSX-ReARMed",          Systems = ["PS1"],                               Recommended = false },
            new() { FileName = "ppsspp_libretro.dll",           DisplayName = "PPSSPP",                Systems = ["PSP"],                               Recommended = true  },
            new() { FileName = "mednafen_pce_libretro.dll",     DisplayName = "Mednafen PCE",          Systems = ["TG16", "TGCD"],                      Recommended = true  },
            new() { FileName = "mednafen_pce_fast_libretro.dll",DisplayName = "Mednafen PCE Fast",     Systems = ["TG16", "TGCD"],                      Recommended = false },
            new() { FileName = "mednafen_ngp_libretro.dll",     DisplayName = "Mednafen Neo Geo Pocket",Systems = ["NGP"],                              Recommended = true  },
            new() { FileName = "stella_libretro.dll",           DisplayName = "Stella",                Systems = ["Atari2600"],                         Recommended = true  },
            new() { FileName = "prosystem_libretro.dll",        DisplayName = "ProSystem",             Systems = ["Atari7800"],                         Recommended = true  },
            new() { FileName = "virtualjaguar_libretro.dll",    DisplayName = "Virtual Jaguar",        Systems = ["Jaguar"],                            Recommended = true  },
            new() { FileName = "gearcoleco_libretro.dll",       DisplayName = "GearColeco",            Systems = ["ColecoVision"],                      Recommended = true  },
            new() { FileName = "freeintv_libretro.dll",         DisplayName = "FreeIntv",              Systems = ["Intellivision"],                     Recommended = true  },
            new() { FileName = "vecx_libretro.dll",             DisplayName = "Vecx",                  Systems = ["Vectrex"],                           Recommended = true  },
            new() { FileName = "opera_libretro.dll",            DisplayName = "Opera (3DO)",            Systems = ["3DO"],                               Recommended = true  },
            new() { FileName = "flycast_libretro.dll",          DisplayName = "Flycast",               Systems = ["Dreamcast"],                         Recommended = true  },
        };

        // ── Infrastructure ────────────────────────────────────────────────────
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private const string BuildbotBase = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";

        private string ZipUrl(string fileName) => BuildbotBase + fileName + ".zip";

        // ── Status check ──────────────────────────────────────────────────────
        /// <summary>
        /// Returns Installed / UpdateAvailable / NotInstalled for a single core.
        /// Uses HTTP HEAD to get Last-Modified and compares against local file write-time.
        /// </summary>
        public async Task<CoreStatus> CheckAsync(CoreEntry entry, string coresFolder,
            CancellationToken ct = default)
        {
            string localPath = Path.Combine(coresFolder, entry.FileName);
            if (!File.Exists(localPath)) return CoreStatus.NotInstalled;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, ZipUrl(entry.FileName));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.Content.Headers.LastModified is DateTimeOffset remote)
                {
                    var local = File.GetLastWriteTimeUtc(localPath);
                    if (remote.UtcDateTime > local.AddMinutes(1))
                        return CoreStatus.UpdateAvailable;
                }
            }
            catch { /* network unavailable — treat as installed */ }

            return CoreStatus.Installed;
        }

        // ── Backup / Revert ───────────────────────────────────────────────────
        public static string BackupPath(string coresFolder, string fileName)
            => Path.Combine(coresFolder, fileName + ".bak");

        public static bool HasBackup(string coresFolder, string fileName)
            => File.Exists(BackupPath(coresFolder, fileName));

        /// <summary>Restores the .bak file, replacing the current .dll.</summary>
        public static void Revert(string coresFolder, string fileName)
        {
            string live   = Path.Combine(coresFolder, fileName);
            string backup = BackupPath(coresFolder, fileName);
            if (!File.Exists(backup))
                throw new FileNotFoundException("No backup found for " + fileName);
            if (File.Exists(live)) File.Delete(live);
            File.Move(backup, live);
        }

        // ── Download ──────────────────────────────────────────────────────────
        /// <summary>
        /// Downloads the zip, backs up the existing .dll to .dll.bak, then extracts.
        /// Reports 0–100 progress.
        /// </summary>
        public async Task DownloadAsync(CoreEntry entry, string coresFolder,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(coresFolder);

            string localPath = Path.Combine(coresFolder, entry.FileName);
            string url       = ZipUrl(entry.FileName);
            string zipPath   = Path.Combine(Path.GetTempPath(), entry.FileName + ".zip");

            // ── Download zip ──
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src  = await resp.Content.ReadAsStreamAsync(ct);
                await using var dest = File.Create(zipPath);

                byte[] buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dest.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                        progress?.Report((int)(downloaded * 100 / total));
                }
            }

            progress?.Report(99);

            // ── Back up existing dll before overwriting ──
            if (File.Exists(localPath))
            {
                string backup = BackupPath(coresFolder, entry.FileName);
                File.Copy(localPath, backup, overwrite: true);
            }

            // ── Extract dll ──
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry2 in zip.Entries)
                {
                    if (entry2.Name.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        entry2.ExtractToFile(localPath, overwrite: true);
                        break;
                    }
                }
            }

            try { File.Delete(zipPath); } catch { }

            progress?.Report(100);
        }
    }
}
