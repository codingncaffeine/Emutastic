using System;
using System.IO;

namespace Emutastic
{
    /// <summary>
    /// Single source of truth for the application data root directory.
    /// Config file normally lives in %AppData%\Emutastic; everything else
    /// (database, saves, snaps, artwork, etc.) lives under DataRoot,
    /// which can be redirected by the user to any folder.
    ///
    /// Portable mode: if a file named "portable.txt" sits next to the .exe,
    /// both config AND data root are forced to [exe]\PortableData\, and the
    /// AppData location is never touched. Toggle is purely opt-in.
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
        /// Detects portable mode by looking for "portable.txt" next to the running .exe.
        /// MUST be called once at the very start of App.OnStartup, before
        /// JsonConfigurationService is constructed. Drop a zero-byte portable.txt next
        /// to the .exe to enable; remove it to revert to AppData behavior.
        /// </summary>
        public static void DetectPortableMode()
        {
            try
            {
                // MainModule path beats AppContext.BaseDirectory because the latter points
                // at the extraction temp dir for single-file published apps (.NET 8) — the
                // user's portable.txt sits next to the .exe, not in the extraction dir.
                string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                string exeDir = !string.IsNullOrEmpty(exePath)
                    ? Path.GetDirectoryName(exePath)!
                    : AppContext.BaseDirectory;
                string marker = Path.Combine(exeDir, "portable.txt");
                if (File.Exists(marker))
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
        public static string GetCoresFolder()
        {
            string folder = _portable && !string.IsNullOrEmpty(_portableRoot)
                ? Path.Combine(_portableRoot, "Cores")
                : Path.Combine(AppContext.BaseDirectory, "Cores");
            Directory.CreateDirectory(folder);
            return folder;
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
