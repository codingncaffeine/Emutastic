using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Emutastic.Services
{
    /// <summary>
    /// Matches Vectrex ROMs to overlay PNGs and manages per-game enable/disable state.
    /// Overlays are enabled by default once downloaded; users can disable per game.
    /// </summary>
    public static class VectrexOverlayService
    {
        private static readonly string SettingsFile = "overlay_settings.json";
        private static Dictionary<string, bool>? _settings;

        /// <summary>
        /// Directory where overlay PNGs are stored.
        /// </summary>
        public static string OverlayDir => AppPaths.GetFolder("Overlays", "Vectrex");

        /// <summary>
        /// True if at least some overlays have been downloaded.
        /// </summary>
        public static bool OverlaysAvailable =>
            Directory.Exists(OverlayDir) && Directory.GetFiles(OverlayDir, "*.png").Length > 0;

        /// <summary>
        /// Finds the best-matching overlay PNG for a given ROM filename.
        /// Returns the full path, or null if no match is found.
        /// </summary>
        public static string? FindOverlay(string romPath)
        {
            if (!OverlaysAvailable) return null;

            string romStem = Path.GetFileNameWithoutExtension(romPath);
            // Strip region tags like "(USA)", "(Europe)", "(Proto)", etc.
            string normalized = NormalizeForMatch(romStem);

            string? bestMatch = null;
            int bestScore = 0;

            foreach (var pngFile in Directory.GetFiles(OverlayDir, "*.png"))
            {
                string overlayStem = Path.GetFileNameWithoutExtension(pngFile);
                string overlayNorm = NormalizeForMatch(overlayStem);

                if (string.Equals(normalized, overlayNorm, StringComparison.OrdinalIgnoreCase))
                    return pngFile; // exact match

                // Partial match — overlay name contained in ROM name or vice versa
                if (normalized.Contains(overlayNorm, StringComparison.OrdinalIgnoreCase) ||
                    overlayNorm.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    int score = overlayNorm.Length;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = pngFile;
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Returns true if the overlay should be shown for this game (enabled by default).
        /// </summary>
        public static bool IsOverlayEnabled(int gameId)
        {
            var settings = LoadSettings();
            string key = gameId.ToString();
            // Default: enabled (true) — only stored when user explicitly disables
            return !settings.TryGetValue(key, out bool enabled) || enabled;
        }

        /// <summary>
        /// Persists the overlay enabled/disabled state for a specific game.
        /// </summary>
        public static void SetOverlayEnabled(int gameId, bool enabled)
        {
            var settings = LoadSettings();
            string key = gameId.ToString();

            if (enabled)
                settings.Remove(key); // default is enabled, so remove explicit entry
            else
                settings[key] = false;

            SaveSettings(settings);
        }

        private static Dictionary<string, bool> LoadSettings()
        {
            if (_settings != null) return _settings;

            string path = Path.Combine(AppPaths.GetFolder("Overlays"), SettingsFile);
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    _settings = JsonSerializer.Deserialize<Dictionary<string, bool>>(json) ?? new();
                }
                catch
                {
                    _settings = new();
                }
            }
            else
            {
                _settings = new();
            }
            return _settings;
        }

        private static void SaveSettings(Dictionary<string, bool> settings)
        {
            _settings = settings;
            string path = Path.Combine(AppPaths.GetFolder("Overlays"), SettingsFile);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Normalizes a filename for fuzzy matching:
        /// strips region/version tags, replaces separators with spaces, lowercases.
        /// </summary>
        private static string NormalizeForMatch(string name)
        {
            // Remove common parenthetical tags: (USA), (Europe), (Proto), (v1.1), etc.
            string result = Regex.Replace(name, @"\s*\([^)]*\)", "");
            // Remove bracket tags: [!], [b1], etc.
            result = Regex.Replace(result, @"\s*\[[^\]]*\]", "");
            // Replace underscores, hyphens, dots with spaces
            result = result.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
            // Collapse multiple spaces
            result = Regex.Replace(result, @"\s+", " ").Trim();
            return result;
        }
    }
}
