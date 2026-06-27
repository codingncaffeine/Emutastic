using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emutastic
{
    /// <summary>
    /// Single source of truth for the application data root directory.
    /// Config file normally lives in %AppData%\Emutastic; everything else
    /// (database, saves, snaps, artwork, etc.) lives under DataRoot,
    /// which can be redirected by the user to any folder.
    ///
    /// Portable mode (two triggers, both opt-in):
    ///   1. Drop a file named "portable.txt" next to the .exe
    ///   2. Pass --portable on the command line
    /// When portable mode is on, both config AND data root are forced to
    /// [exe]\PortableData\, and the AppData location is never touched.
    /// </summary>
    public static class AppPaths
    {
        private static string? _customRoot;
        private static bool _portable;
        private static string? _portableRoot;

        /// <summary>
        /// The default data root: %AppData%\Emutastic.
        /// </summary>
        public static string DefaultRoot { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emutastic");

        /// <summary>True when a portable.txt marker was found next to the .exe.</summary>
        public static bool IsPortable => _portable;

        /// <summary>
        /// Detects portable mode. MUST be called once at the very start of
        /// App.OnStartup, before JsonConfigurationService is constructed.
        /// Triggers (either one activates):
        ///   1. A file named "portable.txt" next to the running .exe.
        ///   2. The --portable command-line argument (case-insensitive).
        /// </summary>
        /// <param name="args">Process command-line args, typically e.Args from
        /// App.OnStartup. Pass null/empty to check only for the marker file.</param>
        public static void DetectPortableMode(string[]? args = null)
        {
            try
            {
                bool cliPortable = args != null && Array.Exists(args,
                    a => string.Equals(a, "--portable", StringComparison.OrdinalIgnoreCase));

                // MainModule path beats AppContext.BaseDirectory because the latter points
                // at the extraction temp dir for single-file published apps (.NET 8) — the
                // user's portable.txt sits next to the .exe, not in the extraction dir.
                string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                string exeDir = !string.IsNullOrEmpty(exePath)
                    ? Path.GetDirectoryName(exePath)!
                    : AppContext.BaseDirectory;
                string marker = Path.Combine(exeDir, "portable.txt");
                bool markerPresent = File.Exists(marker);

                if (cliPortable || markerPresent)
                {
                    _portable = true;
                    _portableRoot = Path.Combine(exeDir, "PortableData");
                    Directory.CreateDirectory(_portableRoot);
                }
            }
            catch
            {
                // Best effort. If the exe dir is read-only we silently fall back to AppData.
                _portable = false;
                _portableRoot = null;
            }
        }

        /// <summary>
        /// The active data root. Portable wins, then custom dir, then default.
        /// </summary>
        public static string DataRoot
        {
            get
            {
                if (_portable && !string.IsNullOrEmpty(_portableRoot))
                {
                    Directory.CreateDirectory(_portableRoot);
                    return _portableRoot;
                }
                if (!string.IsNullOrEmpty(_customRoot))
                {
                    Directory.CreateDirectory(_customRoot);
                    return _customRoot;
                }
                return DefaultRoot;
            }
        }

        /// <summary>
        /// Called once at startup after config is loaded to apply the custom path.
        /// In portable mode the custom path is remembered but DataRoot still points
        /// at PortableData — so removing portable.txt later restores the prior choice.
        /// </summary>
        public static void SetCustomRoot(string? path)
        {
            _customRoot = string.IsNullOrWhiteSpace(path) ? null : path;
        }

        /// <summary>
        /// Relativizes an absolute filesystem path against DataRoot for DB storage.
        /// If the path lives under DataRoot, returns a relative form (e.g. "Roms\NES\zelda.smc")
        /// that survives drive-letter changes when the data folder moves between PCs.
        /// Paths outside DataRoot are returned as-is — they're absolute references the user
        /// owns (e.g. ROMs on a fixed C:\ drive) and we can't make them portable for them.
        /// Empty strings pass through unchanged.
        /// </summary>
        public static string ToStoragePath(string absoluteOrEmpty)
        {
            if (string.IsNullOrEmpty(absoluteOrEmpty)) return absoluteOrEmpty;
            // Already relative? Pass through (idempotent — callers might re-relativize).
            if (!Path.IsPathRooted(absoluteOrEmpty)) return absoluteOrEmpty;
            try
            {
                string dataRoot = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(absoluteOrEmpty);
                string rootWithSep = dataRoot + Path.DirectorySeparatorChar;
                if (full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, dataRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(dataRoot, full);
                }
            }
            catch { /* fall through and store as-is */ }
            return absoluteOrEmpty;
        }

        /// <summary>
        /// Inverse of ToStoragePath. If the stored path is relative, prepends DataRoot;
        /// if absolute, returns as-is. Handles both fresh portable installs (relative paths
        /// in DB) and legacy installs (absolute paths) the same way at the read site.
        /// </summary>
        public static string FromStoragePath(string storedOrEmpty)
        {
            if (string.IsNullOrEmpty(storedOrEmpty)) return storedOrEmpty;
            if (Path.IsPathRooted(storedOrEmpty)) return storedOrEmpty;
            return Path.Combine(DataRoot, storedOrEmpty);
        }

        /// <summary>
        /// Folder that holds libretro core .dlls. In portable mode this lives at
        /// [DataRoot]/Cores/ so the entire portable experience — cores included —
        /// sits inside PortableData/. In normal mode it's [exe]/Cores/ as before.
        /// Cores are downloaded into this folder by CoreManager.
        /// </summary>
        private static bool _coresMigrated;
        private static bool? _exeCoresWritable;

        public static string GetCoresFolder()
        {
            // Cores live next to the real executable in a normal install. But a self-contained
            // single-file build extracts to a per-version %TEMP%\.net\... dir, and every exe-path
            // API can resolve there. Cores must NEVER live in a temporary/extraction dir — it's
            // wiped per version, and external emulators (RPCS3) refuse to run from temp. Fall back to
            // the stable, always-writable per-user data root when the exe folder is temporary OR
            // read-only (e.g. a Program Files install). Portable mode keeps everything in PortableData.
            string folder;
            if (_portable && !string.IsNullOrEmpty(_portableRoot))
                folder = Path.Combine(_portableRoot, "Cores");
            else
            {
                string exeCores = Path.Combine(GetExeFolder(), "Cores");
                _exeCoresWritable ??= CanCreateAndWrite(exeCores);
                folder = (IsTemporaryPath(exeCores) || !_exeCoresWritable.Value)
                    ? Path.Combine(DataRoot, "Cores")
                    : exeCores;
            }
            Directory.CreateDirectory(folder);

            if (!_coresMigrated)
            {
                _coresMigrated = true;
                try { MigrateCoresFromExtractionDirs(folder); } catch { /* best effort */ }
            }
            return folder;
        }

        /// <summary>True when the path lives under the system temp dir or a .NET single-file
        /// extraction dir (…\.net\…) — locations Cores must never be anchored to.</summary>
        private static bool IsTemporaryPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (full.StartsWith(tmp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
                return full.Replace('/', '\\').IndexOf("\\.net\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>True when a directory can be created and written to — used to fall back off a
        /// read-only install location (e.g. Program Files) to the per-user data root.</summary>
        private static bool CanCreateAndWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        // One-time recovery for installs made before the fix above, where cores were written under
        // the single-file extraction directory (a per-version temp folder). Recover any core DLL or
        // emulator subfolder the persistent location is MISSING from a prior extraction directory —
        // a merge, not just an empty-folder fill, so a stranded emulator is recovered even when the
        // libretro cores were already re-downloaded into the persistent folder.
        private static void MigrateCoresFromExtractionDirs(string targetCores)
        {
            if (_portable) return;

            string targetFull = Path.GetFullPath(targetCores).TrimEnd(Path.DirectorySeparatorChar);

            var sources = new List<string> { Path.Combine(AppContext.BaseDirectory, "Cores") };
            try
            {
                string netExtract = Path.Combine(Path.GetTempPath(), ".net");
                if (Directory.Exists(netExtract))
                    foreach (string appDir in Directory.GetDirectories(netExtract))
                        foreach (string verDir in Directory.GetDirectories(appDir))
                            sources.Add(Path.Combine(verDir, "Cores"));
            }
            catch { }

            // Richest prior install first, so the most complete copy wins for any given entry.
            foreach (string src in sources
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(s => Directory.Exists(s) &&
                                     !string.Equals(Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar), targetFull, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(SafeFileCount))
            {
                try
                {
                    foreach (string file in Directory.GetFiles(src, "*.dll", SearchOption.TopDirectoryOnly))
                    {
                        string dest = Path.Combine(targetCores, Path.GetFileName(file));
                        if (!File.Exists(dest)) SafeRelocate(file, dest, isDir: false);
                    }
                    foreach (string dir in Directory.GetDirectories(src))
                    {
                        string dest = Path.Combine(targetCores, Path.GetFileName(dir));
                        if (!Directory.Exists(dest)) SafeRelocate(dir, dest, isDir: true);
                    }
                }
                catch { /* skip this source */ }
            }
        }

        private static int SafeFileCount(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(5000).Count(); }
            catch { return 0; }
        }

        private static void SafeRelocate(string source, string dest, bool isDir)
        {
            try
            {
                if (isDir) { if (!Directory.Exists(dest)) Directory.Move(source, dest); }
                else { if (!File.Exists(dest)) File.Move(source, dest); }
            }
            catch
            {
                try
                {
                    if (isDir) CopyDirectory(source, dest);
                    else File.Copy(source, dest, overwrite: false);
                }
                catch { /* best effort */ }
            }
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dst));
            foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                try { File.Copy(file, file.Replace(src, dst), overwrite: false); } catch { }
            }
        }

        /// <summary>
        /// Folder that holds user-downloaded native assets that aren't bundled in
        /// the release zip — currently SDL3.dll and ffmpeg.exe. Always under
        /// [DataRoot]/Native/ so the files survive both UAC-restricted install
        /// locations (Program Files etc.) and version upgrades where the user
        /// extracts a new release zip into a fresh folder. In portable mode this
        /// resolves to [exe]/PortableData/Native/ so the entire portable bundle
        /// stays self-contained.
        /// </summary>
        public static string GetNativeFolder()
        {
            string folder = Path.Combine(DataRoot, "Native");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Folder that holds DAT files used for CHD/Redump SHA1 lookup during
        /// import. Same persistence rationale as GetNativeFolder.
        /// </summary>
        public static string GetDatsFolder()
        {
            string folder = Path.Combine(DataRoot, "DATs");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Copies a user-picked asset (background image, custom icon, etc.) into
        /// [DataRoot]/{subfolder}/ if it isn't already living under DataRoot, and
        /// returns the new absolute path. If the source is already under DataRoot,
        /// returns it unchanged. Callers should then pass the result through
        /// ToStoragePath before storing in config/DB so the relative form survives
        /// portable USB swaps and CustomDataDirectory changes.
        ///
        /// Collision-safe: if a file with the same name already exists at the
        /// destination, appends "_1", "_2", … to the filename. This avoids
        /// silently overwriting a previously-imported asset the user might still
        /// be using under a different theme/profile.
        /// </summary>
        public static string ImportFileToDataRoot(string sourceAbsolutePath, string subfolder)
        {
            if (string.IsNullOrWhiteSpace(sourceAbsolutePath)) return sourceAbsolutePath;
            if (!File.Exists(sourceAbsolutePath)) return sourceAbsolutePath;

            string dataRoot = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullSrc  = Path.GetFullPath(sourceAbsolutePath);

            // Already under DataRoot — no-op.
            if (fullSrc.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
             || string.Equals(fullSrc, dataRoot, StringComparison.OrdinalIgnoreCase))
                return sourceAbsolutePath;

            string destDir = Path.Combine(dataRoot, subfolder);
            Directory.CreateDirectory(destDir);

            string baseName = Path.GetFileNameWithoutExtension(fullSrc);
            string ext      = Path.GetExtension(fullSrc);
            string destPath = Path.Combine(destDir, baseName + ext);
            // Sanity cap: an ACL or filesystem quirk could in theory keep
            // File.Exists returning true; bail rather than spin forever.
            for (int n = 1; n < 10000 && File.Exists(destPath); n++)
                destPath = Path.Combine(destDir, $"{baseName}_{n}{ext}");

            File.Copy(fullSrc, destPath, overwrite: false);
            return destPath;
        }

        /// <summary>
        /// Returns the .exe folder regardless of portable mode — used by the
        /// native-assets migration to locate any pre-existing SDL3.dll, ffmpeg.exe,
        /// or DATs/ that legacy installs left next to the .exe.
        /// </summary>
        public static string GetExeFolder()
        {
            try
            {
                // Environment.ProcessPath is the real launched executable. For a self-contained
                // single-file build this is the bundle .exe, whereas MainModule and
                // AppContext.BaseDirectory can both resolve to the per-version %TEMP%\.net\
                // extraction dir.
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                return !string.IsNullOrEmpty(exePath)
                    ? Path.GetDirectoryName(exePath)!
                    : AppContext.BaseDirectory;
            }
            catch { return AppContext.BaseDirectory; }
        }

        /// <summary>
        /// In portable mode, returns the path to the .exe folder so we can find
        /// pre-existing Cores/ that shipped with the install (for migration). Null otherwise.
        /// </summary>
        public static string? GetExeFolderIfPortable()
        {
            if (!_portable) return null;
            try
            {
                string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                return !string.IsNullOrEmpty(exePath)
                    ? Path.GetDirectoryName(exePath)
                    : AppContext.BaseDirectory;
            }
            catch { return AppContext.BaseDirectory; }
        }

        // Per-folder overrides (set from Preferences → Folders)
        private static string? _screenshotsRoot;
        private static string? _recordingsRoot;

        public static void SetScreenshotsFolder(string? path)
            => _screenshotsRoot = string.IsNullOrWhiteSpace(path) ? null : path;
        public static void SetRecordingsFolder(string? path)
            => _recordingsRoot = string.IsNullOrWhiteSpace(path) ? null : path;

        /// <summary>
        /// Builds a full path under DataRoot for the given subfolder(s).
        /// Creates the directory if it doesn't exist.
        /// Screenshots and Recordings honour per-folder overrides if set.
        /// </summary>
        public static string GetFolder(params string[] subfolders)
        {
            string root = DataRoot;

            // Check for per-folder overrides — when a custom root is set,
            // it replaces DataRoot + "Screenshots"/"Recordings", so skip the first subfolder
            bool customRoot = false;
            if (subfolders.Length > 0)
            {
                if (subfolders[0] == "Screenshots" && !string.IsNullOrEmpty(_screenshotsRoot))
                { root = _screenshotsRoot; customRoot = true; }
                else if (subfolders[0] == "Recordings" && !string.IsNullOrEmpty(_recordingsRoot))
                { root = _recordingsRoot; customRoot = true; }
            }

            int skip = customRoot ? 1 : 0;
            string[] parts = new string[subfolders.Length - skip + 1];
            parts[0] = root;
            Array.Copy(subfolders, skip, parts, 1, subfolders.Length - skip);
            string path = Path.Combine(parts);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
