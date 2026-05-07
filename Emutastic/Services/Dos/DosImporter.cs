using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Emutastic.Models.Dos;

namespace Emutastic.Services.Dos
{
    /// <summary>
    /// Boxer-style import scanner. Pure inspection of a folder on disk OR a
    /// .zip / .dosz archive (central-directory only — no extraction). No I/O
    /// writes, no UI. Returns a <see cref="DosScanResult"/> describing what
    /// the dropped content looks like (matched profile, main exe, junk
    /// filtered, installers found, etc.).
    ///
    /// Folder scan populates all DosScanResult fields; archive scan populates
    /// only Profile + SuggestedTitle (DOSBox Pure handles archive-mount
    /// internals at launch — we don't extract to inspect inside).
    ///
    /// Caller decides what to do with the result — silent fast-path for the
    /// matched/already-installed case, installer flow when only installers
    /// were detected.
    /// </summary>
    public static class DosImporter
    {
        // Per-batch scan cache. ImportService consults Scan from two different
        // call sites (exe pick + title resolve) for the same folder during a
        // bulk import — without caching that's 2× the disk walks per folder.
        // ConcurrentDictionary (not ThreadStatic) because the import flow does
        // a `Task.Run`-backed pre-pass on a different thread than the main
        // drain loop; ThreadStatic would only dedupe within one thread, which
        // halves the savings for large bulk imports.
        // ResetBatchCache is called at the start of every channel-item drain.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DosScanResult?> _batchCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Clear the scan cache. Called at the start of each import batch by
        /// the import worker so cache lifetime matches batch lifetime.
        /// </summary>
        public static void ResetBatchCache()
        {
            _batchCache.Clear();
        }

        /// <summary>
        /// Scan a folder. Returns null if the path doesn't exist or is empty.
        /// Result is cached process-wide until <see cref="ResetBatchCache"/> is called.
        /// </summary>
        public static DosScanResult? Scan(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return null;

            string key = NormalizeForCacheKey(folderPath);
            return _batchCache.GetOrAdd(key, _ => ScanInternal(folderPath));
        }

        /// <summary>
        /// Scan inside a .zip / .dosz archive (treated identically — .dosz is a
        /// renamed .zip). Reads only the central directory (no extraction), runs
        /// the profile-DB telltale match against entry leaf names. Returns a
        /// minimal <see cref="DosScanResult"/> with the matched profile + a
        /// suggested title; does NOT populate <see cref="DosScanResult.MainExePath"/>
        /// (we don't extract the inner exe path because DOSBox Pure handles
        /// archive mounting + start-menu prompting itself at launch time).
        ///
        /// Cached the same way folder scans are.
        /// </summary>
        public static DosScanResult? ScanArchive(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
                return null;

            string ext = Path.GetExtension(archivePath);
            if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".dosz", StringComparison.OrdinalIgnoreCase))
                return null;

            string key = "archive::" + NormalizeForCacheKey(archivePath);
            return _batchCache.GetOrAdd(key, _ => ScanArchiveInternal(archivePath));
        }

        private static DosScanResult? ScanArchiveInternal(string archivePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                // ZipArchiveEntry.Name is the leaf filename; FullName includes
                // any subdirectory prefix. We match on Name because telltales
                // are leaf-only filenames (e.g. "doom.exe", not "C/DOOM/doom.exe").
                var leafNames = zip.Entries
                    .Select(e => e.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                if (leafNames.Count == 0)
                {
                    Trace.WriteLine($"[DosImporter] archive has no entries: {archivePath}");
                    return null;
                }

                var profile = DosProfileDatabase.Shared.Match(leafNames);
                var result = new DosScanResult
                {
                    ScannedRoot = archivePath,
                    Profile = profile,
                    SuggestedTitle = profile?.Title ?? CleanFolderName(Path.GetFileNameWithoutExtension(archivePath)),
                };

                Trace.WriteLine($"[DosImporter] archive scan {Path.GetFileName(archivePath)}: " +
                                $"profile={profile?.Id ?? "<none>"} entries={leafNames.Count}");
                return result;
            }
            catch (InvalidDataException ex)
            {
                // Corrupt zip — log and bail. Caller will treat as no-profile-match.
                Trace.WriteLine($"[DosImporter] corrupt archive {archivePath}: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DosImporter] archive scan failed {archivePath}: {ex.Message}");
                return null;
            }
        }

        private static string NormalizeForCacheKey(string folderPath)
        {
            try { return Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return folderPath; }
        }

        private static DosScanResult? ScanInternal(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return null;

            var result = new DosScanResult { ScannedRoot = folderPath };

            // 1) Walk top-level + immediate-subfolder files. We don't recurse
            //    deeply because most DOS distributions are flat or have at most
            //    one level of subfolders (DOSBox/, MUSIC/, INSTALL/). The
            //    profile DB telltales are filename-only so deeper recursion
            //    only adds noise.
            var allFiles = EnumerateRelevantFiles(folderPath, result.IgnoredFiles).ToList();

            if (allFiles.Count == 0)
            {
                result.SuggestedTitle = CleanFolderName(folderPath);
                return result;
            }

            // 2) Profile match — telltale lookup against the curated DB.
            var leafNames = allFiles.Select(Path.GetFileName).Where(n => n != null)!.Cast<string>();
            result.Profile = DosProfileDatabase.Shared.Match(leafNames);

            // 3) Suggested title: profile wins, else the cleaned folder name.
            result.SuggestedTitle = result.Profile?.Title ?? CleanFolderName(folderPath);

            // 4) Detect installers and rank them. A profile's IgnoredInstallers
            //    list lets the curated DB say "this game ships with a SETUP.EXE
            //    that's actually an in-game audio configurator — don't treat
            //    it as an installer."
            var ignoredInstallers = result.Profile?.IgnoredInstallers != null
                ? new HashSet<string>(result.Profile.IgnoredInstallers, StringComparer.OrdinalIgnoreCase)
                : null;

            var installerHits = new List<(string Path, int Rank)>();
            foreach (var file in allFiles)
            {
                string leaf = Path.GetFileName(file);
                if (ignoredInstallers != null && ignoredInstallers.Contains(leaf)) continue;
                int? rank = DosImportPolicies.InstallerRank(leaf);
                if (rank.HasValue) installerHits.Add((file, rank.Value));
            }
            result.DetectedInstallers = installerHits
                .OrderBy(t => t.Rank)
                .Select(t => t.Path)
                .ToList();

            // 5) Pick main exe. Profile preferredExe wins (search by leaf name);
            //    else folder-name match; else largest non-utility EXE.
            result.MainExePath = PickMainExe(allFiles, result.Profile, folderPath);

            // 6) Sibling-image auto-mount candidates — siblings of the dropped
            //    folder that look like CD/floppy images.
            result.SuggestedMounts = FindSiblingMounts(folderPath);

            Trace.WriteLine($"[DosImporter] Scanned {folderPath}: profile={result.Profile?.Id ?? "<none>"} " +
                            $"mainExe={Path.GetFileName(result.MainExePath) ?? "<none>"} " +
                            $"installers={result.DetectedInstallers.Count} " +
                            $"junk={result.IgnoredFiles.Count} " +
                            $"mounts={result.SuggestedMounts.Count}");

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static IEnumerable<string> EnumerateRelevantFiles(string root, List<string> ignored)
        {
            foreach (var file in EnumerateFilesShallow(root))
            {
                if (DosImportPolicies.IsJunkFile(file)) { ignored.Add(file); continue; }
                yield return file;
            }
            // One subfolder deep — many DOS games have C/, BIN/, etc.
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(root); }
            catch { yield break; }

            foreach (var sub in subdirs)
            {
                if (DosImportPolicies.IsJunkFolder(sub)) continue;
                foreach (var file in EnumerateFilesShallow(sub))
                {
                    if (DosImportPolicies.IsJunkFile(file)) { ignored.Add(file); continue; }
                    yield return file;
                }
            }
        }

        private static IEnumerable<string> EnumerateFilesShallow(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { return Array.Empty<string>(); }
        }

        private static string? PickMainExe(List<string> allFiles, DosGameProfile? profile, string folderPath)
        {
            // Filter to executables only, excluding utility/installer stems.
            var execs = allFiles
                .Where(f => DosImportPolicies.IsExecutableExtension(Path.GetExtension(f)))
                .Where(f => !DosImportPolicies.InstallerRank(Path.GetFileName(f)).HasValue)
                .ToList();

            if (execs.Count == 0) return null;

            // Profile's preferredExe wins outright.
            if (profile?.PreferredExe != null)
            {
                var match = execs.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), profile.PreferredExe, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // Folder-name match heuristic: prefer e.g. DOOM.EXE in a folder named "Doom".
            string folderLeaf = Path.GetFileName(folderPath);
            var folderMatch = execs.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(folderLeaf, StringComparison.OrdinalIgnoreCase));
            if (folderMatch != null) return folderMatch;

            // Last resort: largest EXE/COM/BAT (game binaries are usually
            // significantly larger than DPMI extenders or sound config tools).
            return execs
                .Select(f => (path: f, size: TryGetSize(f)))
                .OrderByDescending(t => t.size)
                .First().path;
        }

        private static List<string> FindSiblingMounts(string folderPath)
        {
            var mounts = new List<string>();
            try
            {
                string? parent = Path.GetDirectoryName(folderPath);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return mounts;

                string folderLeaf = Path.GetFileName(folderPath);
                foreach (var sibling in Directory.EnumerateFiles(parent, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!DosImportPolicies.IsMountableImage(sibling)) continue;

                    // Only mount siblings whose name begins with our folder name —
                    // avoids picking up unrelated CD images that happen to live in
                    // the same parent dir (very common when users dump everything
                    // into one giant Games/ folder).
                    string siblingStem = Path.GetFileNameWithoutExtension(sibling);
                    if (siblingStem.StartsWith(folderLeaf, StringComparison.OrdinalIgnoreCase) ||
                        folderLeaf.StartsWith(siblingStem, StringComparison.OrdinalIgnoreCase))
                    {
                        mounts.Add(sibling);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DosImporter.FindSiblingMounts] {folderPath}: {ex.Message}");
            }
            return mounts;
        }

        private static string CleanFolderName(string folderPath)
        {
            string leaf = Path.GetFileName(folderPath);
            // Strip trailing version-like markers ("(1.0)", " - DOS", "_v1.5") that
            // make tile names ugly. Keep it conservative — just trim well-known suffixes.
            string[] suffixes = { " (DOS)", " - DOS", " (PC)", "_DOS", "-dos" };
            foreach (var s in suffixes)
                if (leaf.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    leaf = leaf[..^s.Length];
            return leaf.Trim();
        }

        private static long TryGetSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }
    }
}
