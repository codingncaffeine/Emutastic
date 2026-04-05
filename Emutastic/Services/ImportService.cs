using SharpCompress.Archives;
using SharpCompress.Common;
using System.Linq;
using Emutastic.Models;
using Emutastic.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public class ImportService
    {
        private readonly DatabaseService _db;
        private readonly ArtworkService _artwork;
        private readonly CoreManager _coreManager;
        private readonly DatMatchService _datMatcher;

        public ImportService(DatabaseService db, CoreManager coreManager)
        {
            _db = db;
            _artwork = new ArtworkService();
            _coreManager = coreManager;
            _datMatcher = new DatMatchService();
        }

        public event Action<string>? StatusChanged;
        public event Action<Game>? GameImported;

        /// <summary>
        /// Set by the UI layer to resolve ambiguous extensions (e.g. .chd which could be
        /// SegaCD, Saturn, PS1, etc.).  Receives the filename and candidate console tags;
        /// returns the chosen tag, or null if the user cancelled.
        /// </summary>
        public Func<string, string[], Task<string?>>? AmbiguousConsoleResolver { get; set; }

        // Per-folder cache for .bin archives: ask once per folder, apply to the rest.
        private readonly Dictionary<string, string> _folderBinConsole = new(StringComparer.OrdinalIgnoreCase);

        public async Task ImportFilesAsync(IEnumerable<string> filePaths)
        {
            foreach (string path in filePaths)
            {
                if (Directory.Exists(path))
                {
                    await ImportFolderAsync(path);
                    continue;
                }

                if (!File.Exists(path)) continue;

                await ImportSingleRomAsync(path);
            }
        }

        private async Task ImportFolderAsync(string folderPath)
        {
            foreach (string file in Directory.EnumerateFiles(folderPath, "*.*",
                         SearchOption.AllDirectories))
            {
                if (RomService.IsRomFile(file))
                    await ImportSingleRomAsync(file);
            }
        }

        private async Task ImportSingleRomAsync(string romPath)
        {
            string fileName = Path.GetFileName(romPath);
            string ext = Path.GetExtension(romPath);

            // Handle zip / 7z files
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".7z",  StringComparison.OrdinalIgnoreCase))
            {
                // Peek inside to see if it contains a known ROM extension.
                // Arcade ROMs (FBNeo) contain chip dumps with no standard ROM extension,
                // so if nothing recognized is found inside we treat the archive as-is.
                string innerConsole = await DetectConsoleFromZipAsync(romPath);

                // .bin inside an archive is ambiguous — ask once per folder, cache the answer.
                if (innerConsole == "BIN_AMBIGUOUS")
                {
                    string folderKey = Path.GetDirectoryName(romPath) ?? "";
                    if (!_folderBinConsole.TryGetValue(folderKey, out innerConsole!))
                    {
                        var binCandidates = RomService.AmbiguousExtensions[".bin"];
                        string? picked = AmbiguousConsoleResolver == null
                            ? null
                            : await AmbiguousConsoleResolver(fileName, binCandidates);
                        if (picked == null)
                        {
                            StatusChanged?.Invoke($"Skipped {fileName} — console not selected");
                            return;
                        }
                        _folderBinConsole[folderKey] = picked;
                        innerConsole = picked;
                    }
                }

                if (string.IsNullOrEmpty(innerConsole))
                {
                    await ImportRomFileAsync(romPath, "Arcade", fileName);
                    return;
                }

                // Non-arcade archives: extract the single ROM file and re-import it.
                StatusChanged?.Invoke($"Extracting {fileName}…");
                string? extractedPath = await ExtractZipRomAsync(romPath);

                if (extractedPath == null)
                {
                    StatusChanged?.Invoke($"Skipped {fileName} — archive must contain exactly one ROM");
                    return;
                }

                await ImportSingleRomAsync(extractedPath);
                return;
            }

            if (!RomService.IsRomFile(romPath)) return;

            // Ambiguous extension (.chd etc.) — try DAT identification first, picker as fallback.
            var candidates = RomService.GetAmbiguousCandidates(ext);
            if (candidates != null)
            {
                // 1. Try to identify via Redump/No-Intro DAT hash lookup.
                string? autoConsole = null;
                string? autoTitle   = null;

                string? sha1 = ext.Equals(".chd", StringComparison.OrdinalIgnoreCase)
                    ? ChdReader.ReadSha1(romPath)
                    : null;

                if (sha1 != null)
                {
                    var match = _datMatcher.LookupBySha1(sha1);
                    if (match != null)
                    {
                        autoConsole = match.Console;
                        autoTitle   = match.Title;
                        System.Diagnostics.Trace.WriteLine(
                            $"[Import] DAT match: {fileName} → {autoConsole} \"{autoTitle}\"");
                    }
                }

                if (autoConsole != null)
                {
                    await ImportRomFileAsync(romPath, autoConsole, fileName, overrideTitle: autoTitle);
                    return;
                }

                // 2. DAT lookup failed — ask the user.
                if (AmbiguousConsoleResolver == null)
                {
                    StatusChanged?.Invoke($"Skipped {fileName} — could not identify system");
                    return;
                }
                string? picked = await AmbiguousConsoleResolver(fileName, candidates);
                if (picked == null)
                {
                    StatusChanged?.Invoke($"Skipped {fileName} — cancelled");
                    return;
                }
                await ImportRomFileAsync(romPath, picked, fileName);
                return;
            }

            await ImportRomFileAsync(romPath, RomService.DetectConsole(romPath), fileName);
        }

        private async Task ImportRomFileAsync(string romPath, string console, string fileName,
            string? overrideTitle = null)
        {
            if (_db.RomPathExists(romPath)) return;

            StatusChanged?.Invoke($"Importing {fileName}…");

            string manufacturer = RomService.DetectManufacturer(console);
            string title = overrideTitle ?? RomService.CleanTitle(fileName);
            var colors = RomService.GetConsoleColors(console);

            var game = new Game
            {
                Title = title,
                Console = console,
                Manufacturer = manufacturer,
                RomPath = romPath,
                RomHash = string.Empty,
                BackgroundColor = colors.bg,
                AccentColor = colors.accent,
            };

            // Insert immediately so it appears in the library without waiting for hash/artwork
            _db.InsertGame(game);
            GameImported?.Invoke(game);
            StatusChanged?.Invoke($"Added {title} — hashing & fetching artwork…");

            // Hash and artwork fetch in background (hash can be slow for large files)
            _ = Task.Run(async () =>
            {
                string hash = RomService.HashRom(romPath);
                game.RomHash = hash;
                _db.UpdateHash(game.Id, hash);

                var (artworkPath, metadata) = await _artwork.FetchArtworkAsync(hash, romPath, console);

                if (artworkPath != null)
                {
                    _db.UpdateCoverArt(game.Id, artworkPath);
                    game.CoverArtPath = artworkPath;

                    if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                        game.Title = metadata.Title;

                    StatusChanged?.Invoke($"Artwork found for {game.Title}");
                    GameImported?.Invoke(game);
                }
                else
                {
                    StatusChanged?.Invoke($"No artwork found for {title}");
                }
            });
        }

        private static readonly string _importLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenEmuWindows", "import_debug.log");

        private void ImportLog(string message)
        {
            try { File.AppendAllText(_importLogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}\n"); }
            catch { }
        }

        private async Task<string> DetectConsoleFromZipAsync(string archivePath)
        {
            try
            {
                using var archive = ArchiveFactory.Open(archivePath);
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                ImportLog($"[{Path.GetFileName(archivePath)}] {entries.Count} entries: {string.Join(", ", entries.Take(5).Select(e => e.Key ?? "null"))}");
                foreach (var entry in entries)
                {
                    string entryName = entry.Key ?? string.Empty;
                    string ext = Path.GetExtension(entryName);
                    // .bin is ambiguous — signal caller to ask the user rather than guess
                    if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        ImportLog($"  → .bin found, returning BIN_AMBIGUOUS");
                        return "BIN_AMBIGUOUS";
                    }
                    bool recognized = RomService.IsRomExtension(ext);
                    ImportLog($"  entry='{entryName}' ext='{ext}' recognized={recognized}");
                    if (recognized)
                    {
                        string console = RomService.DetectConsole(entryName);
                        ImportLog($"  → console={console}");
                        return console;
                    }
                }
                ImportLog($"  → no ROM extension found, routing to Arcade");
                return string.Empty;
            }
            catch (Exception ex)
            {
                ImportLog($"[{Path.GetFileName(archivePath)}] EXCEPTION: {ex.Message}");
                StatusChanged?.Invoke($"Could not open archive {Path.GetFileName(archivePath)}: {ex.Message}");
                return string.Empty;
            }
        }

        private async Task<bool> CoreSupportsBlockExtractAsync(string console)
        {
            try
            {
                string? corePath = _coreManager.GetCorePath(console);
                if (corePath == null) 
                {
                    System.Diagnostics.Debug.WriteLine($"No core found for console: {console}");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"Checking core block_extract for {console} at {corePath}");
                
                using var core = new LibretroCore(corePath);
                core.Init();
                
                bool blockExtract = core.SystemInfo.block_extract;
                System.Diagnostics.Debug.WriteLine($"Core {console} block_extract: {blockExtract}");
                
                return blockExtract;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking core block_extract for {console}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return false; // Default to extracting if we can't check
            }
        }

        private async Task<string?> ExtractZipRomAsync(string archivePath)
        {
            try
            {
                string tempFolder = Path.Combine(Path.GetTempPath(), "OpenEmuWindows");
                Directory.CreateDirectory(tempFolder);

                using var archive = ArchiveFactory.Open(archivePath);

                var romEntries = new List<IArchiveEntry>();
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    string ext = Path.GetExtension(entry.Key ?? string.Empty);
                    if (RomService.IsRomExtension(ext))
                        romEntries.Add(entry);
                }

                if (romEntries.Count != 1) return null;

                var romEntry = romEntries[0];
                string outputPath = Path.Combine(tempFolder, Path.GetFileName(romEntry.Key!));

                using var inputStream = romEntry.OpenEntryStream();
                using var outputStream = File.Create(outputPath);
                await inputStream.CopyToAsync(outputStream);

                return outputPath;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Extraction failed for {Path.GetFileName(archivePath)}: {ex.Message}");
                return null;
            }
        }
    }
}