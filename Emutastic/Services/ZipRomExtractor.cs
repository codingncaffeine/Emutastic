using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Emutastic.Configuration;
using Emutastic.Services.Archives;

namespace Emutastic.Services
{
    /// <summary>
    /// Extracts the inner ROM from a single-game .zip/.7z/etc. archive into the
    /// app's ExtractedRoms folder. Used by both the importer (so the DB stores the
    /// real path) and the launcher (defensive backstop for DB rows where the .zip
    /// path slipped through, e.g. when imported via the console-nav hint flow before
    /// the hint paths learned to extract).
    /// </summary>
    public static class ZipRomExtractor
    {
        // Consoles whose cores read the archive natively — never extract these.
        // Arcade/NeoGeo ROMs are multi-file chip dumps that must stay as zips.
        private static readonly HashSet<string> ArchiveNativeConsoles =
            new(StringComparer.OrdinalIgnoreCase) { "Arcade", "NeoGeo" };

        public static bool ConsoleNeedsExtraction(string console) =>
            !string.IsNullOrEmpty(console) && !ArchiveNativeConsoles.Contains(console);

        public static bool IsArchiveExtension(string ext) =>
            ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".7z",  StringComparison.OrdinalIgnoreCase);

        public static async Task<string?> ExtractAsync(string archivePath, string console)
        {
            try
            {
                string outputDir = AppPaths.GetFolder("ExtractedRoms", console);
                using var archive = RomArchive.Open(archivePath);

                IRomArchiveEntry? romEntry = null;
                int romCount = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    string innerExt = Path.GetExtension(entry.Key ?? string.Empty);
                    if (RomService.IsRomExtension(innerExt))
                    {
                        romCount++;
                        if (romCount == 1) romEntry = entry;
                    }
                }
                if (romCount != 1 || romEntry == null) return null;

                string outputPath = Path.Combine(outputDir, Path.GetFileName(romEntry.Key!));
                string tmpPath    = outputPath + ".tmp";

                // Fast-path: if the existing file matches the entry size, reuse it.
                // SevenZipExtractor reports Size <= 0 for some formats — skip fast-path then.
                if (romEntry.Size > 0
                    && File.Exists(outputPath)
                    && new FileInfo(outputPath).Length == romEntry.Size)
                    return outputPath;

                if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }

                // Stream directly to disk via ExtractTo — avoids buffering large
                // ROM ISOs (PSP/GC/Wii images can be multiple GB) in memory.
                using (var outputStream = File.Create(tmpPath))
                {
                    romEntry.ExtractTo(outputStream);
                }
                await Task.CompletedTask;

                if (File.Exists(outputPath)) try { File.Delete(outputPath); } catch { }
                File.Move(tmpPath, outputPath);

                return outputPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"ZipRomExtractor: extract failed for {archivePath}: {ex.Message}");
                return null;
            }
        }

        public static string? ExtractSync(string archivePath, string console)
            => ExtractAsync(archivePath, console).GetAwaiter().GetResult();
    }
}
