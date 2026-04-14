using System;
using System.IO;

namespace Emutastic
{
    /// <summary>
    /// Single source of truth for the application data root directory.
    /// Config file always stays in %AppData%\Emutastic; everything else
    /// (database, saves, snaps, artwork, etc.) lives under DataRoot,
    /// which can be redirected by the user to any folder.
    /// </summary>
    public static class AppPaths
    {
        private static string? _customRoot;

        /// <summary>
        /// The default data root: %AppData%\Emutastic.
        /// </summary>
        public static string DefaultRoot { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emutastic");

        /// <summary>
        /// The active data root. Returns the custom directory if set and valid,
        /// otherwise falls back to DefaultRoot.
        /// </summary>
        public static string DataRoot
        {
            get
            {
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
        /// </summary>
        public static void SetCustomRoot(string? path)
        {
            _customRoot = string.IsNullOrWhiteSpace(path) ? null : path;
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
