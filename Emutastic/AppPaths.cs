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

        /// <summary>
        /// Builds a full path under DataRoot for the given subfolder(s).
        /// Creates the directory if it doesn't exist.
        /// </summary>
        public static string GetFolder(params string[] subfolders)
        {
            string[] parts = new string[subfolders.Length + 1];
            parts[0] = DataRoot;
            Array.Copy(subfolders, 0, parts, 1, subfolders.Length);
            string path = Path.Combine(parts);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
