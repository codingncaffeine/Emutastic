using SharpCompress.Archives;
using SharpCompress.Common;
using System.Linq;
using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Configuration;
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
        private readonly IConfigurationService? _configService;

        // Limits concurrent hash+artwork background tasks so SQLite isn't hammered by
        // hundreds of simultaneous writers during a large import (e.g. 200 N64 ROMs).
        private readonly System.Threading.SemaphoreSlim _hashSemaphore = new(6, 6);

        public ImportService(DatabaseService db, CoreManager coreManager,
            IConfigurationService? configService = null)
        {
            _db = db;
            _artwork = new ArtworkService();
            _coreManager = coreManager;
            _datMatcher = new DatMatchService();
            _configService = configService;
        }

        public event Action<string>? StatusChanged;
        public event Action<Game>? GameImported;
        public event Action<int, int>? ProgressChanged; // (current, total)

        private int _progressCurrent;
        private int _progressTotal;
        private int _artworkTotal;
        private int _artworkDone;
        private int _artworkSession; // incremented on each new import; tasks check this before reporting

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
            var paths = filePaths.ToList();

            // Bump session so any still-running background tasks from a previous import
            // stop reporting progress and don't corrupt the new counters.
            int session = System.Threading.Interlocked.Increment(ref _artworkSession);

            // Pre-count all ROM files so we can report accurate progress.
            _progressCurrent = 0;
            _progressTotal   = 0;
            _artworkTotal    = 0;
            _artworkDone     = 0;
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                    _progressTotal += Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                               .Count(RomService.IsRomFile);
                else if (File.Exists(path) && RomService.IsRomFile(path))
                    _progressTotal++;
            }

            ProgressChanged?.Invoke(0, _progressTotal);

            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    await ImportFolderAsync(path);
                    continue;
                }

                if (!File.Exists(path)) continue;

                await ImportSingleRomAsync(path);
                _progressCurrent++;
                ProgressChanged?.Invoke(_progressCurrent, _progressTotal);
            }

            ProgressChanged?.Invoke(_progressTotal, _progressTotal);
        }

        private async Task ImportFolderAsync(string folderPath)
        {
            // If the folder contains archives with .bin files, ask once upfront
            // before importing anything rather than interrupting mid-import.
            bool hasBinArchives = Directory.EnumerateFiles(folderPath, "*.7z", SearchOption.TopDirectoryOnly).Any()
                               || Directory.EnumerateFiles(folderPath, "*.zip", SearchOption.TopDirectoryOnly).Any();

            if (hasBinArchives && !_folderBinConsole.ContainsKey(folderPath))
            {
                // Check folder name first — no dialog needed if we can auto-detect
                string fromFolder = RomService.DetectConsoleFromFolderName(folderPath + Path.DirectorySeparatorChar + "x");
                if (!string.IsNullOrEmpty(fromFolder))
                {
                    _folderBinConsole[folderPath] = fromFolder;
                }
                else
                {
                    // Peek at the first archive to confirm it actually contains .bin
                    string? firstArchive = Directory.EnumerateFiles(folderPath, "*.7z", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.EnumerateFiles(folderPath, "*.zip", SearchOption.TopDirectoryOnly))
                        .FirstOrDefault();

                    if (firstArchive != null)
                    {
                        string detected = await DetectConsoleFromZipAsync(firstArchive);
                        if (detected == "BIN_AMBIGUOUS" && AmbiguousConsoleResolver != null)
                        {
                            string folderName = Path.GetFileName(folderPath);
                            string? picked = await AmbiguousConsoleResolver(
                                $"All games in \"{folderName}\"",
                                RomService.AmbiguousExtensions[".bin"]);
                            if (picked != null)
                                _folderBinConsole[folderPath] = picked;
                        }
                    }
                }
            }

            foreach (string file in Directory.EnumerateFiles(folderPath, "*.*",
                         SearchOption.AllDirectories))
            {
                if (!RomService.IsRomFile(file)) continue;
                await ImportSingleRomAsync(file);
                _progressCurrent++;
                ProgressChanged?.Invoke(_progressCurrent, _progressTotal);
            }
        }

        private async Task ImportSingleRomAsync(string romPath)
        {
            string fileName = Path.GetFileName(romPath);
            string ext = Path.GetExtension(romPath);

            // .bin paired with a .cue in the same folder — skip it; the .cue is the entry point.
            // Checks for ANY .cue in the folder, not just one with the same base name, so that
            // multi-track dumps (Track 01.bin, Track 02.bin, ...) are correctly skipped when
            // only the .cue shares a different naming pattern.
            if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                string folder = Path.GetDirectoryName(romPath) ?? "";
                if (Directory.EnumerateFiles(folder, "*.cue", SearchOption.TopDirectoryOnly).Any())
                    return;
            }

            // Handle zip / 7z files
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".7z",  StringComparison.OrdinalIgnoreCase))
            {
                // Peek inside to see if it contains a known ROM extension.
                // Arcade ROMs (FBNeo) contain chip dumps with no standard ROM extension,
                // so if nothing recognized is found inside we treat the archive as-is.
                string innerConsole = await DetectConsoleFromZipAsync(romPath);

                // BIOS archive (all .rom contents) — skip silently, don't prompt or import.
                if (innerConsole == "BIOS_SKIP")
                {
                    ImportLog($"[{fileName}] SKIPPED — BIOS archive");
                    return;
                }

                // .bin inside an archive is ambiguous — try folder name first, then ask once per folder.
                if (innerConsole == "BIN_AMBIGUOUS")
                {
                    string folderKey = Path.GetDirectoryName(romPath) ?? "";
                    if (!_folderBinConsole.TryGetValue(folderKey, out innerConsole!))
                    {
                        // Try to infer from the folder structure (e.g. "Atari 7800", "Genesis")
                        string fromFolder = RomService.DetectConsoleFromFolderName(romPath);
                        if (!string.IsNullOrEmpty(fromFolder))
                        {
                            _folderBinConsole[folderKey] = fromFolder;
                            innerConsole = fromFolder;
                        }
                        else
                        {
                            // Folder name gave no hint — ask the user once for this folder
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
                }

                // Ambiguous inner extension (e.g. .iso → PSP / GameCube / 3DO) —
                // use the same folder-cache + user-prompt flow as BIN_AMBIGUOUS.
                if (innerConsole.StartsWith("AMBIGUOUS:"))
                {
                    string innerExt = innerConsole.Substring("AMBIGUOUS:".Length);
                    string folderKey = Path.GetDirectoryName(romPath) ?? "";
                    if (!_folderBinConsole.TryGetValue(folderKey, out innerConsole!))
                    {
                        string fromFolder = RomService.DetectConsoleFromFolderName(romPath);
                        var isoCandidates = RomService.GetAmbiguousCandidates(innerExt);
                        if (!string.IsNullOrEmpty(fromFolder) && isoCandidates != null && isoCandidates.Contains(fromFolder))
                        {
                            _folderBinConsole[folderKey] = fromFolder;
                            innerConsole = fromFolder;
                        }
                        else
                        {
                            string? picked = AmbiguousConsoleResolver == null ? null
                                : await AmbiguousConsoleResolver(fileName, isoCandidates ?? Array.Empty<string>());
                            if (picked == null)
                            {
                                StatusChanged?.Invoke($"Skipped {fileName} — console not selected");
                                return;
                            }
                            _folderBinConsole[folderKey] = picked;
                            innerConsole = picked;
                        }
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
                ImportLog($"[{fileName}] extract → {(extractedPath ?? "null (skipped)")}");

                if (extractedPath == null)
                {
                    StatusChanged?.Invoke($"Skipped {fileName} — archive must contain exactly one ROM");
                    return;
                }

                ImportLog($"[{fileName}] RomPathExists={_db.RomPathExists(extractedPath)} → calling ImportRomFileAsync as {innerConsole}");
                await ImportRomFileAsync(extractedPath, innerConsole, Path.GetFileName(extractedPath));
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

                // 2. DAT lookup failed — try folder name before prompting the user.
                string fromFolder = RomService.DetectConsoleFromFolderName(romPath);
                if (!string.IsNullOrEmpty(fromFolder) && candidates.Contains(fromFolder))
                {
                    await ImportRomFileAsync(romPath, fromFolder, fileName);
                    return;
                }

                // 3. Folder name gave no hint — ask the user.
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
            // ── Copy to library folder if configured ──
            var libConfig = _configService?.GetLibraryConfiguration();
            if (libConfig is { CopyToLibrary: true }
                && !string.IsNullOrEmpty(libConfig.LibraryPath))
            {
                try
                {
                    string destDir = libConfig.LibraryPath;
                    if (libConfig.OrganizeByConsole)
                        destDir = Path.Combine(destDir, console);
                    Directory.CreateDirectory(destDir);

                    string destPath = Path.Combine(destDir, Path.GetFileName(romPath));
                    destPath = GetUniqueDestPath(destPath);

                    // Skip copy if the file is already inside the library folder
                    string fullSrc  = Path.GetFullPath(romPath);
                    string fullDest = Path.GetFullPath(destPath);
                    if (!fullSrc.Equals(fullDest, StringComparison.OrdinalIgnoreCase))
                    {
                        StatusChanged?.Invoke($"Copying {Path.GetFileName(romPath)}…");
                        await CopyFileAsync(romPath, destPath);

                        // For .cue files, also copy every .bin referenced inside
                        if (Path.GetExtension(romPath).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                            await CopyCueBinsAsync(romPath, destDir);

                        romPath  = destPath;
                        fileName = Path.GetFileName(destPath);
                    }
                }
                catch (Exception ex)
                {
                    ImportLog($"[{fileName}] COPY FAILED — {ex.Message}");
                    StatusChanged?.Invoke($"Copy failed for {fileName} — importing in-place");
                    // Fall through and import from the original location
                }
            }

            if (_db.RomPathExists(romPath)) { ImportLog($"[{fileName}] SKIPPED — path already in DB"); return; }

            StatusChanged?.Invoke($"Importing {fileName}…");

            string manufacturer = RomService.DetectManufacturer(console);
            string title = overrideTitle ?? RomService.CleanTitle(fileName);

            // NeoGeo: look up full title from DAT (e.g. "samsho" → "Samurai Shodown / Samurai Spirits")
            if (console == "NeoGeo" && overrideTitle == null)
            {
                string romName = Path.GetFileNameWithoutExtension(romPath);
                string? datTitle = _datMatcher.LookupNeoGeoTitle(romName);
                if (datTitle != null) title = datTitle;
            }

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
            ImportLog($"[{fileName}] INSERTED as {console} (id={game.Id})");
            GameImported?.Invoke(game);

            // Reserve a slot in the artwork counter before firing the background task so the
            // denominator is always >= the numerator even if tasks complete out of order.
            System.Threading.Interlocked.Increment(ref _artworkTotal);

            // Capture session so this task can detect if a newer import has started.
            int taskSession = _artworkSession;

            // Hash and artwork fetch in background — semaphore caps concurrent writers to 4
            // so SQLite isn't locked solid during a large bulk import.
            _ = Task.Run(async () =>
            {
                await _hashSemaphore.WaitAsync();
                try
                {
                string hash = RomService.HashRom(romPath);
                game.RomHash = hash;
                _db.UpdateHash(game.Id, hash);

                // Check if another game with the same hash already exists (~ alternate title ROMs).
                // If so, delete this duplicate and skip artwork fetch.
                int? existingId = _db.GetExistingGameIdByHash(hash, console);
                if (existingId != null && existingId.Value != game.Id)
                {
                    _db.DeleteGame(game.Id);
                    ImportLog($"[{System.IO.Path.GetFileName(romPath)}] DUPLICATE of id={existingId.Value}, deleted id={game.Id}");
                    return;
                }

                var (artworkPath, metadata) = await _artwork.FetchArtworkAsync(hash, romPath, console);

                if (artworkPath != null)
                {
                    _db.UpdateCoverArt(game.Id, artworkPath);
                    game.CoverArtPath = artworkPath;

                    if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                        game.Title = metadata.Title;

                    GameImported?.Invoke(game);
                }
                else
                {
                    _db.IncrementArtworkAttempts(game.Id);
                }

                // Only report progress if no newer import has started since this task was spawned.
                if (taskSession == _artworkSession)
                {
                    int done  = System.Threading.Interlocked.Increment(ref _artworkDone);
                    int total = _artworkTotal;
                    int pct   = (int)((done / (double)total) * 100);
                    StatusChanged?.Invoke($"Artwork — {pct}%  ({done} of {total})  {game.Title}");
                }
                }
                finally { _hashSemaphore.Release(); }
            });
        }

        private static readonly string _importLogPath = Path.Combine(
            AppPaths.DataRoot, "import_debug.log");

        private void ImportLog(string message)
        {
            try { File.AppendAllText(_importLogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}\n"); }
            catch { }
        }

        private async Task<string> DetectConsoleFromZipAsync(string archivePath)
        {
            await Task.CompletedTask; // satisfy CS1998; method is intentionally synchronous
            try
            {
                using var archive = ArchiveFactory.Open(archivePath);
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                ImportLog($"[{Path.GetFileName(archivePath)}] {entries.Count} entries: {string.Join(", ", entries.Take(5).Select(e => e.Key ?? "null"))}");
                // If every file inside is a .rom, this is a BIOS archive — skip silently.
                if (entries.Count > 0 && entries.All(e =>
                        Path.GetExtension(e.Key ?? string.Empty)
                            .Equals(".rom", StringComparison.OrdinalIgnoreCase)))
                {
                    ImportLog($"  → all entries are .rom — treating as BIOS archive, skipping");
                    return "BIOS_SKIP";
                }

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
                        // DetectConsole returns "Unknown" for ambiguous extensions (.iso, .cue, etc.)
                        // that live in AmbiguousExtensions rather than ExtensionMap.
                        var candidates = RomService.GetAmbiguousCandidates(ext);
                        if (candidates != null || console == "Unknown" || string.IsNullOrEmpty(console))
                        {
                            // Ambiguous extension inside archive (e.g. .iso, .cue) —
                            // try folder name before falling back to asking the user.
                            string fromFolder = RomService.DetectConsoleFromFolderName(archivePath);
                            if (candidates != null && candidates.Contains(fromFolder))
                            {
                                console = fromFolder;
                            }
                            else if (candidates != null)
                            {
                                // Folder name gave no hint — signal caller to ask user
                                ImportLog($"  → ambiguous {ext}, returning AMBIGUOUS signal");
                                return $"AMBIGUOUS:{ext}";
                            }
                            else
                            {
                                console = fromFolder;
                            }
                        }
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

        private Task<bool> CoreSupportsBlockExtractAsync(string console)
        {
            try
            {
                string? corePath = _coreManager.GetCorePath(console);
                if (corePath == null)
                {
                    System.Diagnostics.Debug.WriteLine($"No core found for console: {console}");
                    return Task.FromResult(false);
                }

                System.Diagnostics.Debug.WriteLine($"Checking core block_extract for {console} at {corePath}");

                using var core = new LibretroCore(corePath);
                core.Init();

                bool blockExtract = core.SystemInfo.block_extract;
                System.Diagnostics.Debug.WriteLine($"Core {console} block_extract: {blockExtract}");

                return Task.FromResult(blockExtract);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking core block_extract for {console}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return Task.FromResult(false); // Default to extracting if we can't check
            }
        }

        private async Task<string?> ExtractZipRomAsync(string archivePath)
        {
            try
            {
                string tempFolder = Path.Combine(Path.GetTempPath(), "Emutastic");
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

        // ── Copy-to-library helpers ───────────────────────────────────────────

        private static async Task CopyFileAsync(string source, string dest)
        {
            const int bufferSize = 81920; // 80 KB — good balance for HDD/SSD
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize, useAsync: true);
            using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize, useAsync: true);
            await src.CopyToAsync(dst);
        }

        /// <summary>
        /// Parses a .cue sheet and copies every referenced .bin file into destDir.
        /// </summary>
        private async Task CopyCueBinsAsync(string cuePath, string destDir)
        {
            string? cueDir = Path.GetDirectoryName(cuePath);
            if (cueDir == null) return;

            foreach (string line in File.ReadLines(cuePath))
            {
                // FILE "Track 01.bin" BINARY
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? binName = ParseCueFileName(trimmed);
                if (binName == null) continue;

                string binSrc  = Path.Combine(cueDir, binName);
                string binDest = Path.Combine(destDir, binName);

                if (!File.Exists(binSrc)) continue;
                if (File.Exists(binDest)) continue; // already there

                StatusChanged?.Invoke($"Copying {binName}…");
                await CopyFileAsync(binSrc, binDest);
            }
        }

        /// <summary>Extracts the filename from a CUE FILE directive.</summary>
        private static string? ParseCueFileName(string fileLine)
        {
            // FILE "some file.bin" BINARY  or  FILE somefile.bin BINARY
            int start = fileLine.IndexOf('"');
            if (start >= 0)
            {
                int end = fileLine.IndexOf('"', start + 1);
                if (end > start)
                    return fileLine.Substring(start + 1, end - start - 1);
            }
            // Unquoted: FILE name.bin BINARY
            string[] parts = fileLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : null;
        }

        /// <summary>
        /// Returns destPath as-is if it doesn't exist. Otherwise appends (2), (3), etc.
        /// </summary>
        private static string GetUniqueDestPath(string destPath)
        {
            if (!File.Exists(destPath)) return destPath;

            string dir  = Path.GetDirectoryName(destPath)!;
            string name = Path.GetFileNameWithoutExtension(destPath);
            string ext  = Path.GetExtension(destPath);

            for (int i = 2; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return destPath; // extremely unlikely fallback
        }
    }
}