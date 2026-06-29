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
        // Optional custom download (a .zip containing the .dll). Null means the
        // libretro buildbot. Used for cores we publish ourselves (e.g. Tigerbyte).
        public string? Url        { get; init; }
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
            new() { FileName = "azahar_libretro.dll",          DisplayName = "Azahar (3DS)",          Systems = ["3DS"],                               Recommended = true  },
            new() { FileName = "mednafen_vb_libretro.dll",      DisplayName = "Mednafen Virtual Boy",  Systems = ["VirtualBoy"],                        Recommended = true  },
            new() { FileName = "genesis_plus_gx_libretro.dll",  DisplayName = "Genesis Plus GX",       Systems = ["Genesis", "SegaCD", "SMS", "GameGear", "SG1000"], Recommended = true  },
            new() { FileName = "picodrive_libretro.dll",        DisplayName = "PicoDrive",             Systems = ["Genesis", "Sega32X", "SMS"],         Recommended = true  },
            new() { FileName = "mednafen_saturn_libretro.dll",  DisplayName = "Mednafen Saturn (Beetle)", Systems = ["Saturn"],                         Recommended = true  },
            new() { FileName = "kronos_libretro.dll",           DisplayName = "Kronos",                Systems = ["Saturn"],                            Recommended = false },
            new() { FileName = "yabause_libretro.dll",          DisplayName = "Yabause",               Systems = ["Saturn"],                            Recommended = false },
            new() { FileName = "mednafen_psx_hw_libretro.dll",  DisplayName = "Mednafen PSX HW (Beetle)", Systems = ["PS1"],                            Recommended = true  },
            new() { FileName = "mednafen_psx_libretro.dll",     DisplayName = "Mednafen PSX (Beetle)", Systems = ["PS1"],                               Recommended = false },
            new() { FileName = "pcsx2_libretro.dll",            DisplayName = "LRPS2 (PCSX2)",         Systems = ["PS2"],                               Recommended = true  },
            new() { FileName = "ppsspp_libretro.dll",           DisplayName = "PPSSPP",                Systems = ["PSP"],                               Recommended = true  },
            new() { FileName = "mednafen_pce_libretro.dll",     DisplayName = "Mednafen PCE",          Systems = ["TG16", "TGCD"],                      Recommended = true  },
            new() { FileName = "mednafen_pce_fast_libretro.dll",DisplayName = "Mednafen PCE Fast",     Systems = ["TG16", "TGCD"],                      Recommended = false },
            new() { FileName = "mednafen_ngp_libretro.dll",     DisplayName = "Mednafen Neo Geo Pocket",Systems = ["NGP"],                              Recommended = true  },
            new() { FileName = "stella_libretro.dll",           DisplayName = "Stella",                Systems = ["Atari2600"],                         Recommended = true  },

            new() { FileName = "prosystem_libretro.dll",        DisplayName = "ProSystem",             Systems = ["Atari7800"],                         Recommended = true  },
            new() { FileName = "virtualjaguar_libretro.dll",    DisplayName = "Virtual Jaguar",        Systems = ["Jaguar"],                            Recommended = true  },
            new() { FileName = "gearcoleco_libretro.dll",       DisplayName = "GearColeco",            Systems = ["ColecoVision"],                      Recommended = true  },
            new() { FileName = "bluemsx_libretro.dll",          DisplayName = "BlueMSX",               Systems = ["ColecoVision"],                      Recommended = false },

            new() { FileName = "vecx_libretro.dll",             DisplayName = "Vecx",                  Systems = ["Vectrex"],                           Recommended = true  },
            new() { FileName = "opera_libretro.dll",            DisplayName = "Opera (3DO)",            Systems = ["3DO"],                               Recommended = true  },
            new() { FileName = "same_cdi_libretro.dll",         DisplayName = "SAME CDi",              Systems = ["CDi"],                               Recommended = true  },
            new() { FileName = "flycast_libretro.dll",          DisplayName = "Flycast",               Systems = ["Dreamcast"],                         Recommended = true  },

            // Arcade
            new() { FileName = "fbneo_libretro.dll",            DisplayName = "FBNeo (Final Burn Neo)", Systems = ["Arcade"],                            Recommended = true  },
            new() { FileName = "mame2003_plus_libretro.dll",    DisplayName = "MAME 2003-Plus",         Systems = ["Arcade"],                            Recommended = true  },
            new() { FileName = "geolith_libretro.dll",         DisplayName = "Geolith (Neo Geo / CD)", Systems = ["NeoGeo", "NeoCD"],                   Recommended = true  },

            // Tiger Game.com — our own from-scratch core, published via GitHub releases (not on the buildbot)
            new() { FileName = "tigerbyte_libretro.dll",        DisplayName = "Tigerbyte (Game.com)",   Systems = ["GameCom"],                           Recommended = true,
                    Url = "https://github.com/codingncaffeine/Tigerbyte/releases/latest/download/tigerbyte_libretro.dll.zip" },
        };

        // ── Infrastructure ────────────────────────────────────────────────────
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private const string BuildbotBase = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";

        private string ZipUrl(CoreEntry entry) => entry.Url ?? (BuildbotBase + entry.FileName + ".zip");

        // ── Status check ──────────────────────────────────────────────────────

        /// <summary>
        /// Threshold for flagging a core as "update available". The buildbot
        /// publishes nightlies, so any installed file is technically older
        /// than the latest by hours — flagging on every drift would train
        /// users to ignore the badge. Only flag when the remote is meaningfully
        /// newer than the local file.
        /// </summary>
        public static readonly TimeSpan StalenessThreshold = TimeSpan.FromDays(14);

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
                using var req = new HttpRequestMessage(HttpMethod.Head, ZipUrl(entry));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.Content.Headers.LastModified is DateTimeOffset remote)
                {
                    var local = File.GetLastWriteTimeUtc(localPath);
                    if (remote.UtcDateTime > local.Add(StalenessThreshold))
                        return CoreStatus.UpdateAvailable;
                }
            }
            catch { /* network unavailable — treat as installed */ }

            return CoreStatus.Installed;
        }

        /// <summary>
        /// Fans out CheckAsync across every catalog entry that's actually
        /// installed locally. Returns the subset whose remote build is at
        /// least <see cref="StalenessThreshold"/> newer than the local file.
        /// Network failures are swallowed (returned as Installed in CheckAsync),
        /// so an offline run produces an empty list rather than throwing.
        /// </summary>
        public async Task<List<CoreEntry>> CheckAllForUpdatesAsync(string coresFolder,
            CancellationToken ct = default)
        {
            var installed = Catalog
                .Where(e => File.Exists(Path.Combine(coresFolder, e.FileName)))
                .ToList();

            // Parallel HEAD requests — buildbot is a CDN, this is fine.
            var tasks = installed.Select(async e =>
            {
                var status = await CheckAsync(e, coresFolder, ct).ConfigureAwait(false);
                return (entry: e, status);
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results
                .Where(r => r.status == CoreStatus.UpdateAvailable)
                .Select(r => r.entry)
                .ToList();
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

        // A freshly-downloaded core must be a real Windows PE image before we accept
        // it: "MZ" DOS header plus a valid "PE\0\0" signature at the e_lfanew offset.
        // Cheap fail-safe so a corrupt or wrong-content download is never left in
        // place to be loaded in-process.
        private static bool IsPortableExecutable(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 1024) return false;   // a real core is tens of KB+
                using var fs = File.OpenRead(path);
                using var br = new System.IO.BinaryReader(fs);
                if (br.ReadByte() != (byte)'M' || br.ReadByte() != (byte)'Z') return false;
                fs.Seek(0x3C, SeekOrigin.Begin);                    // e_lfanew → PE header offset
                int peOffset = br.ReadInt32();
                if (peOffset <= 0 || peOffset > fi.Length - 4) return false;
                fs.Seek(peOffset, SeekOrigin.Begin);
                return br.ReadByte() == (byte)'P' && br.ReadByte() == (byte)'E'
                    && br.ReadByte() == 0 && br.ReadByte() == 0;
            }
            catch { return false; }
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
            string url       = ZipUrl(entry);
            // Cores are native code we load in-process — only ever fetch them over
            // TLS (the libretro buildbot, or a trusted HTTPS release URL). Fail closed
            // if a URL is ever misconfigured to plain http.
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to download a core over a non-HTTPS URL: {url}");
            string zipPath   = Path.Combine(Path.GetTempPath(), entry.FileName + ".zip");
            string phase     = "init";

            void Trace(string msg)
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [CoreDownload] {entry.FileName} {msg}";
                System.Diagnostics.Trace.WriteLine(line);
                try
                {
                    string logDir = AppPaths.GetFolder("Logs");
                    string logPath = Path.Combine(logDir, "cores.log");
                    LogRotation.RotateIfLarge(logPath);
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
                catch { /* non-fatal */ }
            }

            try
            {
                Trace($"start url={url} localPath={localPath}");

                // ── Download zip ──
                phase = "http-get";
                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    Trace($"http-status={(int)resp.StatusCode} {resp.StatusCode} content-length={resp.Content.Headers.ContentLength}");
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    long downloaded = 0;

                    phase = "http-stream";
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
                    Trace($"downloaded={downloaded} bytes to {zipPath}");
                }

                progress?.Report(99);

                // ── Back up existing dll before overwriting ──
                phase = "backup";
                if (File.Exists(localPath))
                {
                    string backup = BackupPath(coresFolder, entry.FileName);
                    File.Copy(localPath, backup, overwrite: true);
                    Trace($"backup created at {backup}");
                }

                // ── Extract dll ──
                phase = "extract";
                bool extracted = false;
                string[] zipEntryNames;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    zipEntryNames = zip.Entries.Select(e => e.FullName).ToArray();
                    foreach (var entry2 in zip.Entries)
                    {
                        if (entry2.Name.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase))
                        {
                            entry2.ExtractToFile(localPath, overwrite: true);
                            extracted = true;
                            break;
                        }
                    }
                }

                if (!extracted)
                {
                    string contents = string.Join(", ", zipEntryNames);
                    Trace($"NO MATCHING ENTRY in zip — entries: [{contents}]");
                    throw new InvalidDataException(
                        $"Zip did not contain '{entry.FileName}'. Contents: [{contents}]");
                }

                // Semantic gate: the HTTPS transfer + zip CRC already fail safe on a
                // corrupted download, but verify the extracted file is actually a
                // valid PE image before we keep it. On failure, restore the previous
                // core from the backup we just made so a bad download never replaces
                // a working core. (Authenticity against a buildbot compromise would
                // need libretro-signed cores, which don't exist — HTTPS to the
                // official buildbot is the trust anchor, same posture as RetroArch.)
                phase = "validate";
                if (!IsPortableExecutable(localPath))
                {
                    Trace("extracted file is not a valid PE image — rejecting");
                    try { File.Delete(localPath); } catch { }
                    string bak = BackupPath(coresFolder, entry.FileName);
                    if (File.Exists(bak))
                    {
                        try { File.Copy(bak, localPath, overwrite: true); Trace("restored previous core from backup"); }
                        catch (Exception ex) { Trace($"backup restore failed: {ex.Message}"); }
                    }
                    throw new InvalidDataException(
                        $"Downloaded '{entry.FileName}' is not a valid core binary — kept the previous version.");
                }

                // ZipArchiveEntry.ExtractToFile preserves the entry's internal
                // LastWriteTime — stamp with now so the 14-day staleness check
                // is grounded in install time.
                phase = "stamp-mtime";
                try { File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow); }
                catch (Exception ex)
                {
                    Trace($"could not stamp mtime: {ex.Message}");
                }

                Trace($"extracted=true mtimeUtc={File.GetLastWriteTimeUtc(localPath):o}");

                phase = "cleanup";
                try { File.Delete(zipPath); } catch { }

                progress?.Report(100);
            }
            catch (Exception ex)
            {
                Trace($"FAILED in phase={phase} type={ex.GetType().FullName} msg={ex.Message}");
                if (ex.InnerException != null)
                    Trace($"  inner type={ex.InnerException.GetType().FullName} msg={ex.InnerException.Message}");
                throw;
            }
        }
    }
}
