using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Emutastic.Models;

namespace Emutastic.Services
{
    /// <summary>
    /// Manages the libretro community cheats database. Downloads the single
    /// `cheats.zip` from the libretro buildbot (~37 MB, updated nightly),
    /// extracts only the systems Emutastic supports, and provides per-game
    /// lookup keyed by ROM filename.
    ///
    /// One-shot download approach (vs per-file via the GitHub API) to avoid
    /// rate-limit headaches and keep the user-facing UX simple — one button,
    /// one progress bar, one downloaded artifact.
    /// </summary>
    public static class CheatDatabaseService
    {
        public const string CheatsZipUrl = "https://buildbot.libretro.com/assets/frontend/cheats.zip";

        // Maps the libretro-database upstream folder name → our internal console tag.
        // Folders not in this map are skipped during extraction so we don't bloat
        // the user's disk with cheats for systems Emutastic can't run anyway.
        private static readonly Dictionary<string, string> _folderToConsole = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Nintendo - Nintendo Entertainment System"]        = "NES",
            ["Nintendo - Family Computer Disk System"]          = "FDS",
            ["Nintendo - Super Nintendo Entertainment System"]  = "SNES",
            ["Nintendo - Nintendo 64"]                          = "N64",
            ["Nintendo - Game Boy"]                             = "GB",
            ["Nintendo - Game Boy Color"]                       = "GBC",
            ["Nintendo - Game Boy Advance"]                     = "GBA",
            ["Nintendo - Nintendo DS"]                          = "NDS",
            ["Sega - Mega Drive - Genesis"]                     = "Genesis",
            ["Sega - Mega-CD - Sega CD"]                        = "SegaCD",
            ["Sega - 32X"]                                      = "Sega32X",
            ["Sega - Saturn"]                                   = "Saturn",
            ["Sega - Master System - Mark III"]                 = "SMS",
            ["Sega - Game Gear"]                                = "GameGear",
            ["Sega - Dreamcast"]                                = "Dreamcast",
            ["Sony - PlayStation"]                              = "PS1",
            ["Sony - PlayStation Portable"]                     = "PSP",
            ["NEC - PC Engine - TurboGrafx 16"]                 = "TG16",
            ["NEC - PC Engine CD - TurboGrafx-CD"]              = "TGCD",
            ["Atari - 2600"]                                    = "Atari2600",
            ["Atari - 7800"]                                    = "Atari7800",
            ["Atari - Jaguar"]                                  = "Jaguar",
            ["Coleco - ColecoVision"]                           = "ColecoVision",
            ["FBNeo - Arcade Games"]                            = "Arcade",
            ["SNK - Neo Geo"]                                   = "NeoGeo",
            ["SNK - Neo Geo CD"]                                = "NeoCD",
        };

        /// <summary>Local root for the extracted cheat database.</summary>
        public static string DatabaseRoot => AppPaths.GetFolder("CheatsDatabase");

        /// <summary>Local directory for one console's cheat files.</summary>
        public static string LocalDirFor(string console) =>
            AppPaths.GetFolder("CheatsDatabase", console);

        /// <summary>Marker file written after a successful download — used to
        /// surface "Installed" state in Preferences and to detect updates.</summary>
        private static string VersionMarkerPath => Path.Combine(DatabaseRoot, ".installed");

        /// <summary>True when the database has been downloaded at least once.</summary>
        public static bool IsInstalled() => File.Exists(VersionMarkerPath);

        /// <summary>Total .cht file count across all extracted system folders.</summary>
        public static int TotalFileCount()
        {
            try
            {
                if (!Directory.Exists(DatabaseRoot)) return 0;
                int count = 0;
                foreach (var dir in Directory.EnumerateDirectories(DatabaseRoot))
                    count += Directory.GetFiles(dir, "*.cht").Length;
                return count;
            }
            catch { return 0; }
        }

        /// <summary>Number of distinct console folders with at least one .cht file.</summary>
        public static int InstalledSystemCount()
        {
            try
            {
                if (!Directory.Exists(DatabaseRoot)) return 0;
                int n = 0;
                foreach (var dir in Directory.EnumerateDirectories(DatabaseRoot))
                    if (Directory.GetFiles(dir, "*.cht").Length > 0) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Downloads cheats.zip from the libretro buildbot and extracts only
        /// the systems Emutastic supports into [DataRoot]/CheatsDatabase/.
        /// progress callback fires for both download (0..90) and extract
        /// (90..100) phases with a status message.
        /// </summary>
        public static async Task<int> DownloadAndExtractAsync(
            Action<int, string>? progress = null)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Emutastic");
            http.Timeout = TimeSpan.FromMinutes(5);

            // ── 1. Download cheats.zip with byte-level progress ──────────────
            progress?.Invoke(0, "Connecting…");
            using var resp = await http.GetAsync(CheatsZipUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? 0;
            using var ms = new MemoryStream(capacity: total > 0 ? (int)total : 40 * 1024 * 1024);
            using (var stream = await resp.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    received += read;
                    int pct = total > 0
                        ? (int)Math.Min(90, received * 90 / total)
                        : 45;
                    progress?.Invoke(pct, $"Downloading… {received / (1024 * 1024)} MB" +
                                          (total > 0 ? $" / {total / (1024 * 1024)} MB" : ""));
                }
            }

            // ── 2. Extract just the .cht files for supported systems ────────
            progress?.Invoke(91, "Extracting…");
            ms.Position = 0;
            int extracted = 0;
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                    if (!entry.FullName.EndsWith(".cht", StringComparison.OrdinalIgnoreCase)) continue;

                    // Entry path is normally just `{folder}/{file}.cht`; some zips
                    // wrap in a top-level dir. Locate the {folder}/{file} segment
                    // by taking the last two path components.
                    var parts = entry.FullName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    string folder   = parts[parts.Length - 2];
                    string fileName = parts[parts.Length - 1];

                    if (!_folderToConsole.TryGetValue(folder, out var consoleTag)) continue;

                    var localDir = LocalDirFor(consoleTag);
                    var localPath = Path.Combine(localDir, fileName);

                    using var src = entry.Open();
                    using var dst = File.Create(localPath);
                    await src.CopyToAsync(dst);
                    extracted++;
                }
            }

            // Marker file with timestamp so future "Update" runs can show what
            // version is installed.
            try { await File.WriteAllTextAsync(VersionMarkerPath, DateTime.UtcNow.ToString("o")); }
            catch { /* non-critical */ }

            progress?.Invoke(100, $"Done — {extracted} cheat files for {InstalledSystemCount()} systems");
            return extracted;
        }

        /// <summary>Result of an import lookup. SkippedActionReplay is kept
        /// in the API surface for forward compatibility but is always 0
        /// now that the frontend AR-write path handles those codes.</summary>
        public class LookupResult
        {
            public List<Cheat> Cheats { get; init; } = new();
            public int SkippedActionReplay { get; init; }
        }

        /// <summary>
        /// Looks up cheats for a game by matching its ROM filename (without
        /// extension) against locally-extracted .cht files. Falls back to
        /// region-stripped title prefix match when the exact filename misses.
        /// AR codes are imported normally — EmulatorWindow's per-frame
        /// ApplyFrontendArToRam handles them via direct system-RAM writes,
        /// matching what RetroArch does for its "RetroArch handled" cheats.
        /// </summary>
        public static LookupResult? LookupForGame(Game game)
        {
            if (game == null || string.IsNullOrEmpty(game.Console) || string.IsNullOrEmpty(game.RomPath))
                return null;

            var dir = LocalDirFor(game.Console);
            if (!Directory.Exists(dir)) return null;

            var candidates = Directory.GetFiles(dir, "*.cht");
            if (candidates.Length == 0) return null;

            var stem = Path.GetFileNameWithoutExtension(game.RomPath);

            // 1) Exact filename match.
            string? match = candidates.FirstOrDefault(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), stem, StringComparison.OrdinalIgnoreCase));

            // 2) Region/version-stripped prefix match.
            if (match == null)
            {
                string norm = NormalizeTitle(stem);
                if (norm.Length > 0)
                {
                    match = candidates.FirstOrDefault(p =>
                        NormalizeTitle(Path.GetFileNameWithoutExtension(p))
                            .Equals(norm, StringComparison.OrdinalIgnoreCase));
                }
            }

            return match == null ? null : new LookupResult { Cheats = ParseChtFile(match) };
        }

        /// <summary>
        /// Parses a libretro .cht file (cheats = N / cheatN_desc / cheatN_code /
        /// cheatN_enable). Multi-line codes joined with "+" are preserved as-is —
        /// genesis_plus_gx and other cores split on `+` themselves.
        /// </summary>
        public static List<Cheat> ParseChtFile(string path)
        {
            var result = new List<Cheat>();
            try
            {
                var lines = File.ReadAllLines(path);
                var entries = new Dictionary<int, Cheat>();

                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key   = line[..eq].Trim();
                    var value = line[(eq + 1)..].Trim().Trim('"');

                    if (!key.StartsWith("cheat", StringComparison.OrdinalIgnoreCase)) continue;

                    int us = key.IndexOf('_');
                    if (us <= 5) continue;
                    if (!int.TryParse(key.Substring(5, us - 5), out int idx)) continue;

                    var field = key.Substring(us + 1);
                    if (!entries.TryGetValue(idx, out var ch))
                    {
                        ch = new Cheat();
                        entries[idx] = ch;
                    }

                    switch (field.ToLowerInvariant())
                    {
                        case "desc":   ch.Title = value; break;
                        case "code":   ch.Code  = value; break;
                        case "enable": ch.Enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }

                foreach (var kvp in entries.OrderBy(k => k.Key))
                {
                    if (string.IsNullOrEmpty(kvp.Value.Code)) continue;
                    // Force disabled-by-default on import so the user opts in.
                    kvp.Value.Enabled = false;
                    result.Add(kvp.Value);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Cheats DB] ParseChtFile({path}) failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Strips the (region) / [tag] / version markers from a title so
        /// "Sonic the Hedgehog (USA)" matches "Sonic the Hedgehog (World)".
        /// </summary>
        private static string NormalizeTitle(string s)
        {
            int paren   = s.IndexOf('(');
            int bracket = s.IndexOf('[');
            int cut = -1;
            if (paren   >= 0) cut = paren;
            if (bracket >= 0 && (cut < 0 || bracket < cut)) cut = bracket;
            if (cut >= 0) s = s[..cut];
            return s.Trim();
        }
    }
}
