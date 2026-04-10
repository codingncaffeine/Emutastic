using Emutastic.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Emutastic.Services
{
    public class ScreenshotService
    {
        private readonly string _folder;

        public ScreenshotService()
        {
            _folder = AppPaths.GetFolder("Screenshots");
        }

        /// <summary>
        /// Saves a BitmapSource as a PNG.
        /// Filename format: {yyyyMMdd_HHmmss} {GameTitle} ({Console}).png
        /// Returns the saved file path, or null on failure.
        /// </summary>
        public string? Save(BitmapSource bitmap, string gameTitle, string console)
        {
            try
            {
                string safeTitle   = SanitizeFileName(gameTitle);
                string safeConsole = SanitizeFileName(console);
                string timestamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName    = $"{timestamp} {safeTitle} ({safeConsole}).png";
                string consoleFolder = AppPaths.GetFolder("Screenshots", safeConsole);
                string filePath    = Path.Combine(consoleFolder, fileName);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.OpenWrite(filePath);
                encoder.Save(stream);

                return filePath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Screenshot] Save failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns all saved screenshots, newest first.
        /// Parses metadata from the filename.
        /// </summary>
        public List<Screenshot> GetAll()
        {
            var results = new List<Screenshot>();

            foreach (string file in Directory.EnumerateFiles(_folder, "*.png", SearchOption.AllDirectories)
                                             .OrderByDescending(f => f))
            {
                var ss = ParseFileName(file);
                if (ss != null) results.Add(ss);
            }

            return results;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static Screenshot? ParseFileName(string filePath)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                // Expected: "{yyyyMMdd_HHmmss} {title} ({console})"
                if (name.Length < 16) return null;

                string timestampStr = name[..15];  // "yyyyMMdd_HHmmss"
                if (!DateTime.TryParseExact(timestampStr, "yyyyMMdd_HHmmss",
                        null, System.Globalization.DateTimeStyles.None, out DateTime takenAt))
                    return null;

                string rest = name[16..]; // "{title} ({console})"

                // Extract console from last "(…)"
                int parenOpen  = rest.LastIndexOf('(');
                int parenClose = rest.LastIndexOf(')');
                if (parenOpen < 0 || parenClose < parenOpen) return null;

                string console    = rest[(parenOpen + 1)..parenClose].Trim();
                string gameTitle  = rest[..parenOpen].Trim();

                return new Screenshot
                {
                    FilePath  = filePath,
                    GameTitle = gameTitle,
                    Console   = console,
                    TakenAt   = takenAt,
                };
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}
