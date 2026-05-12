using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emutastic.Services
{
    /// <summary>
    /// Detects multi-disc CD games inside a folder and bundles their disc files
    /// into an .m3u playlist so the libretro disc-control interface sees every
    /// disc as a separate image. Without this, importing "Game (Disc 1).cue" +
    /// "Game (Disc 2).cue" lands in the library as two unrelated games and the
    /// in-game disk-swap chord is a no-op (only one image per session).
    ///
    /// Amiga / PUAE caveat: PUAE supports multi-drive setups (DF0–DF3) but plain
    /// .m3u entries land in DF0 only. Per-drive distribution requires `(MD)`
    /// filename markers on each entry (PUAE convention) or the
    /// `puae_floppy_multidrive=enabled` core option. We don't generate `(MD)`
    /// markers here — works for swap-style multi-disk games, may fall short on
    /// games that need DF1 mounted simultaneously with DF0.
    ///
    /// Detection is filename-pattern only; we don't open the files. Common forms:
    ///   "Game Title (USA) (Disc 1).cue"
    ///   "Game Title (USA) (Disc 2 of 3).cue"
    ///   "Game Title (Disk 1).chd"
    ///   "Game Title - CD1.cue"
    ///   "Game Title (CD 2).cue"
    /// The regex captures the disc number; the rest of the filename (with the
    /// disc tag removed) becomes the group key.
    /// </summary>
    public static class M3uBundler
    {
        // Match (Disc N), (Disk N), (CD N), - CDN, etc. The number is captured.
        // "of M" suffix tolerated. Case insensitive.
        private static readonly Regex DiscTag = new(
            @"[\s\-_]*[\(\[]?\s*(?:disc|disk|cd)\s*(\d+)(?:\s*of\s*\d+)?\s*[\)\]]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Disc-image extensions that are valid M3U entries. .bin is excluded
        // because cue/chd is the entry point — bins are referenced by cues.
        private static readonly HashSet<string> DiscExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".cue", ".chd", ".iso", ".ccd", ".mds", ".gdi", ".m3u",
                // GameCube image formats — Dolphin libretro supports m3u disc-swap
                // for multi-disc titles (Resident Evil 0, Baten Kaitos, etc.).
                ".rvz", ".gcm", ".ciso", ".wbfs", ".gcz", ".wia",
                // Dreamcast .cdi format — Flycast supports m3u disc-swap (Skies
                // of Arcadia spans 2 discs, etc.).
                ".cdi",
            };

        public sealed record DiscEntry(string Path, int DiscNumber);
        public sealed record Bundle(string GroupKey, string BaseTitle, List<DiscEntry> Discs);

        /// <summary>
        /// Group disc files in the given list by base title (filename minus the
        /// disc tag). Returns one bundle per group with 2+ discs; single-disc
        /// groups are NOT returned (caller imports those normally).
        /// </summary>
        public static List<Bundle> FindBundles(IEnumerable<string> filePaths)
        {
            var groups = new Dictionary<string, Bundle>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in filePaths)
            {
                string ext = Path.GetExtension(path);
                if (!DiscExtensions.Contains(ext)) continue;
                if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase)) continue; // skip existing playlists

                string nameNoExt = Path.GetFileNameWithoutExtension(path);
                var match = DiscTag.Match(nameNoExt);
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups[1].Value, out int discNum)) continue;

                string baseTitle = nameNoExt.Substring(0, match.Index)
                                            + nameNoExt.Substring(match.Index + match.Length);
                baseTitle = baseTitle.Trim();
                // Reject suspiciously short base titles like "" / "cd" / "x" — these
                // come from filenames such as `disc1.cue` or `cd2.chd` where there's
                // no real game name, and we'd risk falsely merging unrelated dumps.
                if (baseTitle.Length < 3) continue;
                // Reject when the disc tag isn't near the end of the filename — real
                // multi-disc dumps virtually always end with the disc tag (possibly
                // followed by region/rev parens). If "(Disc 1)" appears mid-title
                // it's more likely to be in the title itself ("Audio CD 32 Sampler")
                // than a real disc indicator.
                int tail = nameNoExt.Length - (match.Index + match.Length);
                if (tail > 30) continue;
                // Group key: folder + baseTitle + ext (so different formats in same
                // folder don't merge — e.g. .cue set and .chd set kept separate).
                string folder = Path.GetDirectoryName(path) ?? "";
                string key = $"{folder}|{baseTitle}|{ext}".ToLowerInvariant();

                if (!groups.TryGetValue(key, out var bundle))
                {
                    bundle = new Bundle(key, baseTitle, new List<DiscEntry>());
                    groups[key] = bundle;
                }
                bundle.Discs.Add(new DiscEntry(path, discNum));
            }

            // Sort discs and filter to contiguous-from-1 sequences. Rejected groups
            // are logged so users have a breadcrumb when their multi-disc set lands
            // in the library as separate games (e.g. user is missing disc 1, or the
            // dumps have non-contiguous numbering).
            var kept = new List<Bundle>();
            foreach (var b in groups.Values)
            {
                if (b.Discs.Count < 2) continue;
                b.Discs.Sort((a, c) => a.DiscNumber.CompareTo(c.DiscNumber));
                bool contiguousFromOne = true;
                for (int i = 0; i < b.Discs.Count; i++)
                {
                    if (b.Discs[i].DiscNumber != i + 1) { contiguousFromOne = false; break; }
                }
                if (contiguousFromOne) { kept.Add(b); continue; }

                string seq = string.Join(",", b.Discs.Select(d => d.DiscNumber));
                System.Diagnostics.Trace.WriteLine(
                    $"[M3U] '{b.BaseTitle}' rejected — discs found ({seq}) aren't contiguous from 1; " +
                    $"importing as individual games. Add the missing disc and re-import to bundle.");
            }
            return kept;
        }

        /// <summary>
        /// Write an .m3u playlist next to the disc files (relative paths) and
        /// return its absolute path. If a playlist with the same name already
        /// exists, leave it alone — the user may have hand-curated it.
        /// </summary>
        public static string WritePlaylist(Bundle bundle)
        {
            string folder = Path.GetDirectoryName(bundle.Discs[0].Path) ?? "";
            string m3uName = bundle.BaseTitle + ".m3u";
            string m3uPath = Path.Combine(folder, m3uName);
            // Never clobber an existing .m3u — user-authored playlists win.
            if (File.Exists(m3uPath)) return m3uPath;

            string content = string.Join(Environment.NewLine,
                bundle.Discs.Select(d => Path.GetFileName(d.Path)));
            File.WriteAllText(m3uPath, content + Environment.NewLine);
            return m3uPath;
        }

        /// <summary>
        /// Parses an existing .m3u playlist and returns absolute paths of every
        /// disc file it references. Used by the importer to skip those files so
        /// `Game.m3u` + `Game (Disc 1).cue` + `Game (Disc 2).cue` produces a
        /// single library entry (the .m3u), not three. Honors both user-authored
        /// playlists and the ones we auto-generate. Mirrors RetroArch's manual
        /// scanner convention (`tasks/task_database.c task_database_iterate_m3u`):
        /// an M3U is authoritative over the cues it lists. '#' lines are comments
        /// per spec; blank lines skipped.
        /// </summary>
        /// <summary>
        /// Reads a .cue sheet and returns absolute paths of every FILE the cue
        /// references (typically .bin tracks). Used by the importer to widen the
        /// skip set when a cue file is bundled — the cue's .bin sidecars must also
        /// be hidden from per-file import or they'd land in the library individually.
        /// </summary>
        public static IEnumerable<string> GetCueReferencedAbsolutePaths(string cuePath)
        {
            string cueDir;
            try { cueDir = Path.GetDirectoryName(cuePath) ?? ""; }
            catch { yield break; }

            string[] lines;
            try { lines = File.ReadAllLines(cuePath); }
            catch { yield break; }

            foreach (string raw in lines)
            {
                string line = raw.TrimStart();
                if (!line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;
                // FILE "Track 01.bin" BINARY  or  FILE somefile.bin BINARY
                string? name = null;
                int q = line.IndexOf('"');
                if (q >= 0)
                {
                    int q2 = line.IndexOf('"', q + 1);
                    if (q2 > q) name = line.Substring(q + 1, q2 - q - 1);
                }
                if (name == null)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) name = parts[1];
                }
                if (string.IsNullOrEmpty(name)) continue;
                string full;
                try
                {
                    full = Path.IsPathRooted(name) ? name : Path.Combine(cueDir, name);
                    full = Path.GetFullPath(full);
                }
                catch { continue; }
                yield return full;
            }
        }

        public static IEnumerable<string> GetReferencedAbsolutePaths(string m3uPath)
        {
            string m3uDir;
            try { m3uDir = Path.GetDirectoryName(m3uPath) ?? ""; }
            catch { yield break; }

            string[] lines;
            try { lines = File.ReadAllLines(m3uPath); }
            catch { yield break; }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string entry;
                try
                {
                    entry = Path.IsPathRooted(line) ? line : Path.Combine(m3uDir, line);
                    entry = Path.GetFullPath(entry);
                }
                catch { continue; }
                yield return entry;
            }
        }
    }
}
