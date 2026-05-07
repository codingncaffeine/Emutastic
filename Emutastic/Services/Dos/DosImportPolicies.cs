using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emutastic.Services.Dos
{
    /// <summary>
    /// Path / filename predicates ported from Boxer's
    /// <c>BXImportSession+BXImportPolicies.m</c>. Pure functions; no I/O or state.
    ///
    /// References:
    ///   https://github.com/alinebee/Boxer/blob/master/Boxer/BXImportSession+BXImportPolicies.h
    ///   https://github.com/alinebee/Boxer/blob/master/Boxer/BXImportSession+BXImportPolicies.m
    /// </summary>
    public static class DosImportPolicies
    {
        // ── Junk filter ──────────────────────────────────────────────────────
        // Files we silently drop from consideration before any other logic runs.
        // Mirrors Boxer's +ignoredFilePatterns / +junkFilePatterns: DirectX redists,
        // GOG launcher chrome, README files, archiver utilities, .pif shortcuts,
        // and DOSBox sub-installs left in repacked distributions.
        // Match is case-insensitive on the filename (no path), regex.
        private static readonly Regex[] JunkFilenamePatterns =
        {
            new(@"^dxsetup\.exe$",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^dotnetfx.*\.exe$",         RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^vcredist.*\.exe$",         RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^directx.*\.(exe|cab|inf)$",RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^gog(setup|com).*\.exe$",   RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^goggame.*\.(dll|info)$",   RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^unins\d*\.(exe|dat|msg)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^thumbs\.db$",              RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^\.ds_store$",              RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^readme.*\.(txt|md|1st)$",  RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^license.*\.txt$",          RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"\.pif$",                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^arj\.exe$",                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^pkunzip\.exe$",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^lha\.exe$",                RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        // Folder names whose entire contents are junk (skipped during scan).
        // Lower-cased compare against folder leaf name.
        private static readonly HashSet<string> JunkFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "directx",
            "_commonredist",
            "redist",
            "univbe",
            "tafe", // GOG launcher leftover
            "__macosx",
        };

        public static bool IsJunkFile(string fileName)
        {
            string leaf = Path.GetFileName(fileName);
            foreach (var pattern in JunkFilenamePatterns)
                if (pattern.IsMatch(leaf)) return true;
            return false;
        }

        public static bool IsJunkFolder(string folderName)
        {
            string leaf = Path.GetFileName(folderName);
            return JunkFolderNames.Contains(leaf);
        }

        // ── Installer detection (ranked) ─────────────────────────────────────
        // Boxer's +preferredInstallerPatterns and +installerPatterns rank
        // installer-shaped exe names so the "best" one wins when multiple exist
        // (e.g. CD ships INSTALL.EXE *and* SETUP.EXE — INSTALL wins).
        //
        // Lower rank value = higher preference.
        private static readonly (Regex Pattern, int Rank)[] InstallerPatterns =
        {
            (new(@"^dosinst",   RegexOptions.IgnoreCase | RegexOptions.Compiled), 0),
            (new(@"^install\.", RegexOptions.IgnoreCase | RegexOptions.Compiled), 1),
            (new(@"^hdinstal",  RegexOptions.IgnoreCase | RegexOptions.Compiled), 2),
            (new(@"^setup\.",   RegexOptions.IgnoreCase | RegexOptions.Compiled), 3),
            (new(@"^install",   RegexOptions.IgnoreCase | RegexOptions.Compiled), 4),
            (new(@"^setup",     RegexOptions.IgnoreCase | RegexOptions.Compiled), 5),
            (new(@"inst",       RegexOptions.IgnoreCase | RegexOptions.Compiled), 6),
        };

        /// <summary>
        /// Returns the installer rank for a filename (lower = stronger match), or
        /// null if the filename doesn't look like an installer.
        /// </summary>
        public static int? InstallerRank(string fileName)
        {
            string leaf = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            if (!IsExecutableExtension(ext)) return null;
            foreach (var (pattern, rank) in InstallerPatterns)
                if (pattern.IsMatch(leaf + ext)) return rank;
            return null;
        }

        // ── Already-installed telltales (extension-based fallback) ───────────
        // Boxer's +playableGameTelltaleExtensions: presence of *any* file with
        // these extensions in the source means "this is already installed; skip
        // the installer flow." Our profile DB telltales win first; this is the
        // fallback heuristic for games not in the curated DB.
        private static readonly HashSet<string> PlayableTelltaleExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cdrom", ".harddisk", ".floppy",  // Boxer drive-folder magic extensions
            ".gog", ".cue", ".iso", ".cdr",   // CD images that suggest "ready to mount and play"
            ".m3u8",                           // multi-disc playlist
            ".dosbox", ".dosz",                // DOSBox-Pure native bundle
        };

        public static bool HasPlayableTelltaleExtension(string fileName)
        {
            return PlayableTelltaleExtensions.Contains(Path.GetExtension(fileName));
        }

        // ── Sibling-mount detection ──────────────────────────────────────────
        // When a game folder has a sibling .iso / .cue / .img, mount it as an
        // additional drive at launch. Mirrors Boxer's auto-mount behaviour.
        private static readonly HashSet<string> MountableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".iso", ".cue", ".img", ".cdr", ".bin",
        };

        public static bool IsMountableImage(string fileName)
        {
            return MountableExtensions.Contains(Path.GetExtension(fileName));
        }

        // ── Executable extensions ────────────────────────────────────────────
        public static bool IsExecutableExtension(string ext)
        {
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".com", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        }

        // ── CD-sized threshold ───────────────────────────────────────────────
        // Boxer's BXCDROMSizeThreshold (~100 MB). Folders >= this are treated as
        // CD-distribution layouts (mount as D:); below = floppy-distribution (A:).
        public const long CDSizeThresholdBytes = 100L * 1024 * 1024;

        /// <summary>
        /// Estimates total size of a folder by enumerating top-level files only
        /// (cheap; CD-sized layouts have most data at the top).
        /// </summary>
        public static long FolderTopLevelSize(string folderPath)
        {
            try
            {
                return Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }
    }
}
