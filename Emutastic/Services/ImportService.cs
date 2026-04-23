using SharpCompress.Archives;
using SharpCompress.Common;
using System.Linq;
using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
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

        // Pre-loaded at import start — avoids per-ROM DB queries for duplicate checking.
        private HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);

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
        public event Action? ImportQueueDrained; // fired when all queued batches finish

        private int _progressCurrent;
        private int _progressTotal;
        private int _artworkTotal;
        private int _artworkDone;
        private volatile int _drainGeneration; // artwork tasks check this to avoid corrupting new cycle's counters

        // ── Serial import queue (OpenEmu-style) ──────────────────────────
        // New imports are appended; a single background worker drains them in order.
        private readonly Channel<List<string>> _importQueue =
            Channel.CreateUnbounded<List<string>>(new UnboundedChannelOptions { SingleReader = true });
        private Task? _importWorker;
        private readonly object _workerLock = new();
        public volatile bool IsImporting;

        /// <summary>
        /// Set by the UI layer to resolve ambiguous extensions (e.g. .chd which could be
        /// SegaCD, Saturn, PS1, etc.).  Receives the filename and candidate console tags;
        /// returns the chosen tag, or null if the user cancelled.
        /// </summary>
        public Func<string, string[], Task<string?>>? AmbiguousConsoleResolver { get; set; }

        // Per-folder cache for .bin archives: ask once per folder, apply to the rest.
        private readonly Dictionary<string, string> _folderBinConsole = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Enqueues paths for import. If an import is already running the new batch
        /// is appended to the queue and progress counters accumulate (OpenEmu-style).
        /// Returns immediately — the actual work happens on a background worker.
        /// </summary>
        public void ImportFilesAsync(IEnumerable<string> filePaths)
        {
            var paths = filePaths.ToList();
            if (paths.Count == 0) return;

            lock (_workerLock)
            {
                _importQueue.Writer.TryWrite(paths);

                if (_importWorker == null || _importWorker.IsCompleted)
                    _importWorker = Task.Run(ProcessImportQueueAsync);
            }
        }

        private async Task ProcessImportQueueAsync()
        {
            IsImporting = true;

            // Bump generation so stale artwork tasks from a previous drain don't touch our counters.
            Interlocked.Increment(ref _drainGeneration);

            // Reset counters at the start of a new queue drain.
            _progressCurrent = 0;
            _progressTotal   = 0;
            _artworkTotal    = 0;
            _artworkDone     = 0;

            // Pre-load known paths once per queue drain.
            _knownPaths = _db.GetAllRomPaths();

            // Drain loop: process available batches, then wait briefly for more.
            // The 200ms coalescing window lets rapid drag-and-drops merge into one drain.
            while (true)
            {
                if (!_importQueue.Reader.TryRead(out var paths))
                {
                    // Nothing ready — wait up to 200ms for a new batch before exiting.
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                    try
                    {
                        if (!await _importQueue.Reader.WaitToReadAsync(cts.Token))
                            break; // Channel completed (shouldn't happen with unbounded)
                        continue;  // Item available — loop back to TryRead
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Timeout — no more batches, we're done
                    }
                }

                StatusChanged?.Invoke("Scanning files…");

                // Count new files and add to running total.
                int batchCount = 0;
                await Task.Run(() =>
                {
                    foreach (string path in paths)
                    {
                        if (Directory.Exists(path))
                        {
                            var (files, _) = PrepareFolderImport(path);
                            batchCount += files.Count;
                        }
                        else if (File.Exists(path) && RomService.IsRomFile(path))
                            batchCount++;
                    }
                });

                Interlocked.Add(ref _progressTotal, batchCount);
                ProgressChanged?.Invoke(_progressCurrent, _progressTotal);

                // Process this batch.
                foreach (string path in paths)
                {
                    if (Directory.Exists(path))
                    {
                        await ImportFolderAsync(path);
                        continue;
                    }

                    if (!File.Exists(path)) continue;

                    await ImportSingleRomAsync(path);
                    Interlocked.Increment(ref _progressCurrent);
                    ProgressChanged?.Invoke(_progressCurrent, _progressTotal);
                }
            }

            ProgressChanged?.Invoke(_progressTotal, _progressTotal);
            IsImporting = false;
            ImportQueueDrained?.Invoke();
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

            var (filesToImport, dosOverrides) = PrepareFolderImport(folderPath);

            foreach (string file in filesToImport)
            {
                if (dosOverrides.TryGetValue(file, out string? overrideTitle))
                    await ImportRomFileAsync(file, "DOS", Path.GetFileName(file), overrideTitle: overrideTitle);
                else
                    await ImportSingleRomAsync(file);

                Interlocked.Increment(ref _progressCurrent);
                ProgressChanged?.Invoke(_progressCurrent, _progressTotal);
            }
        }

        /// <summary>
        /// Enumerates importable ROM files in a folder, collapsing multi-executable
        /// DOS game installs (e.g. Dungeon Hack/ with DHACK.EXE + SETUP.EXE + DOS4GW.EXE)
        /// down to one entry per folder. Returns the filtered file list plus a map
        /// of DOS entry-point paths → folder-name title overrides so artwork lookup
        /// uses the game name rather than the executable stem.
        /// </summary>
        private static (List<string> files, Dictionary<string, string> dosOverrides)
            PrepareFolderImport(string folderPath)
        {
            var allRoms = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                                   .Where(RomService.IsRomFile)
                                   .ToList();

            // Group DOS executables (.exe/.com/.bat) by their containing folder.
            var dosByFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in allRoms)
            {
                if (!IsDosExecutable(f)) continue;
                string folder = Path.GetDirectoryName(f) ?? string.Empty;
                if (!dosByFolder.TryGetValue(folder, out var list))
                    dosByFolder[folder] = list = new List<string>();
                list.Add(f);
            }

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Pass 1: pick one exe per containing folder.
            var folderPicks = new List<(string folder, string chosenExe, string? title)>();

            foreach (var kvp in dosByFolder)
            {
                string folder = kvp.Key;
                var exes = kvp.Value;
                string? gameTitle = ResolveDosGameFolderName(folder, folderPath);

                if (exes.Count == 1)
                {
                    string onlyExe = exes[0];
                    bool isSubfolder = !string.Equals(folder, folderPath, StringComparison.OrdinalIgnoreCase);
                    // Only apply override when we have a real game-title resolution;
                    // at the top of a raw scan, onlyExe stands on its own.
                    string? title = (isSubfolder && !string.IsNullOrWhiteSpace(gameTitle)) ? gameTitle : null;
                    folderPicks.Add((folder, onlyExe, title));
                    continue;
                }

                string chosen = DosEntryPointPicker.Pick(folder, exes);
                string resolvedTitle = string.IsNullOrWhiteSpace(gameTitle)
                    ? Path.GetFileNameWithoutExtension(chosen)
                    : gameTitle;
                folderPicks.Add((folder, chosen, resolvedTitle));

                foreach (string f in exes)
                    if (!string.Equals(f, chosen, StringComparison.OrdinalIgnoreCase))
                        skip.Add(f);
            }

            // Pass 2: merge picks sharing the same game-root folder. A GOG install
            // typically has "<Game>/dosbox.exe" + "<Game>/C/GAME.EXE" + often a pile
            // of "<Game>/C/VESA/<VENDOR>/*.exe" video-driver probes — all one game.
            // Within a bucket, discard utility picks (dosbox.exe, VESA probes) and
            // prefer the shallowest remaining pick (the real game binary in "C/").
            var bucketsByRoot = new Dictionary<string, List<(string folder, string exe, string? title, int depth)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (folder, exe, title) in folderPicks)
            {
                string? rootPath = ResolveDosGameRootPath(folder, folderPath);
                string key = rootPath ?? folder;
                int depth = folder.Count(c => c == Path.DirectorySeparatorChar);

                if (!bucketsByRoot.TryGetValue(key, out var list))
                    bucketsByRoot[key] = list = new List<(string, string, string?, int)>();
                list.Add((folder, exe, title, depth));
            }

            foreach (var (_, picks) in bucketsByRoot)
            {
                var nonUtil = picks.Where(p => !DosEntryPointPicker.IsUtility(p.exe)).ToList();
                if (nonUtil.Count == 0) nonUtil = picks; // every pick is a utility — keep best of them

                var chosen = nonUtil
                    .OrderBy(p => p.depth)
                    .ThenByDescending(p => DosEntryPointPicker.TryGetSize(p.exe))
                    .First();

                // Prefer a folder-derived title from ANY pick in the bucket (the
                // top-level pick knows the real game folder name even if the C/-level
                // pick couldn't resolve it).
                string? title = picks.Select(p => p.title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                if (!string.IsNullOrWhiteSpace(title))
                    overrides[chosen.exe] = title;

                foreach (var p in picks)
                    if (!string.Equals(p.exe, chosen.exe, StringComparison.OrdinalIgnoreCase))
                        skip.Add(p.exe);
            }

            var filtered = allRoms.Where(f => !skip.Contains(f)).ToList();
            return (filtered, overrides);
        }

        private static bool IsDosExecutable(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".com", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        }

        // Folder names that shadow the DOS drive letter or are bulk containers —
        // never the actual game name. Walk past them when resolving a game title.
        private static readonly HashSet<string> DosShadowFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "dos", "dos games", "doslib", "games", "game", "pc", "pcgames",
            "bin", "program files", "programs",
        };

        private static bool IsShadowFolder(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return true;
            if (folderName.Length <= 1) return true; // drive-letter shadows (C, D, etc.)
            return DosShadowFolderNames.Contains(folderName);
        }

        /// <summary>
        /// Walks up from <paramref name="exeFolder"/> (the folder containing the chosen
        /// DOS executable) toward <paramref name="scanRoot"/>, skipping drive-letter
        /// shadow folders like "C" and bulk-dirs like "DOS" / "games". Returns the
        /// first folder name that looks like a real game title, or null if nothing
        /// suitable is found before reaching the scan root.
        /// </summary>
        private static string? ResolveDosGameFolderName(string exeFolder, string scanRoot)
        {
            string? path = ResolveDosGameRootPath(exeFolder, scanRoot);
            if (string.IsNullOrEmpty(path)) return null;
            string name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Prefer the curated DOS catalog's canonical title if the folder
            // name matches a known entry. Falls back to the raw folder name.
            var entry = DosGameListService.Match(name);
            return entry?.Title ?? name;
        }

        /// <summary>
        /// Resolves the "game root" for a DOS executable folder. If the user imported
        /// a per-game folder (e.g. "Sim City 2000"), every nested exe — even under
        /// VESA/AHEAD/ — buckets into that one game root. If they imported a bulk
        /// directory (e.g. "dos games"), each immediate subfolder becomes its own root.
        /// Two picks sharing this path are collapsed into a single game entry.
        /// </summary>
        private static string? ResolveDosGameRootPath(string exeFolder, string scanRoot)
        {
            string scanRootFull   = Path.GetFullPath(scanRoot).TrimEnd(Path.DirectorySeparatorChar);
            string exeFolderFull  = Path.GetFullPath(exeFolder).TrimEnd(Path.DirectorySeparatorChar);
            string scanRootName   = Path.GetFileName(scanRootFull);
            bool scanRootIsBulk   = IsShadowFolder(scanRootName);

            // Per-game scan: scanRoot itself is the game folder. Everything under
            // it collapses to one entry regardless of nesting.
            if (!scanRootIsBulk)
                return string.IsNullOrWhiteSpace(scanRootName) ? null : scanRootFull;

            // Bulk scan: game root = immediate child of scanRoot on the path to exeFolder.
            if (exeFolderFull.Equals(scanRootFull, StringComparison.OrdinalIgnoreCase))
                return scanRootFull; // exe is directly in the bulk dir — unusual, fall back

            string current = exeFolderFull;
            while (true)
            {
                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent)) return null;
                string parentFull = parent.TrimEnd(Path.DirectorySeparatorChar);
                if (parentFull.Equals(scanRootFull, StringComparison.OrdinalIgnoreCase))
                    return current;
                current = parentFull;
            }
        }

        /// <summary>
        /// Chooses the likely launch executable from a folder containing multiple
        /// DOS executables. Filters out common utility/installer stems, then prefers
        /// the exe whose basename matches the folder name, falling back to the largest file.
        /// </summary>
        private static class DosEntryPointPicker
        {
            // Common DOS install / utility executables that should not be treated as
            // the main game entry point. Matched case-insensitively on the filename stem.
            private static readonly HashSet<string> UtilityStems = new(StringComparer.OrdinalIgnoreCase)
            {
                "setup", "config", "configure",
                "dos4gw", "cwsdpmi", "dpmi32vm", "pmodew", "pmode", "dos32a",
                "deice", "readme", "view", "register", "order",
                "sbpro", "sbconfig", "sbset", "ultramid", "ultrinit",
                "help", "info", "runme", "unpack", "unzip", "arj",
                // Launchers / wrappers commonly shipped alongside the actual game
                "dosbox", "dosbox-x", "!start", "start", "launch", "autoexec",
                "autodet", "detect", "test", "scan",
                // Redistributables sometimes left in GOG install dirs
                "vcredist", "vcredist_x86", "vcredist_x64", "dotnetfx", "directx", "dxsetup",
            };

            public static string Pick(string folder, List<string> exes)
            {
                var candidates = exes.Where(f => !IsUtility(f)).ToList();
                if (candidates.Count == 0) candidates = exes;

                // Prefer exe whose stem matches the folder name.
                string folderName = Path.GetFileName(folder);
                var folderMatch = candidates.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f)
                        .Equals(folderName, StringComparison.OrdinalIgnoreCase));
                if (folderMatch != null) return folderMatch;

                // Otherwise pick the largest file — main game binaries are almost
                // always significantly larger than DPMI extenders or SB setup utilities.
                return candidates
                    .Select(f => (path: f, size: TryGetSize(f)))
                    .OrderByDescending(t => t.size)
                    .First().path;
            }

            public static bool IsUtility(string path)
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (UtilityStems.Contains(stem)) return true;
                if (stem.StartsWith("uninst",  StringComparison.OrdinalIgnoreCase)) return true;
                if (stem.StartsWith("install", StringComparison.OrdinalIgnoreCase)) return true;
                // VESA-probe utilities shipped with mid-90s DOS games in VESA\<vendor>\*
                if (stem.EndsWith("vesa", StringComparison.OrdinalIgnoreCase)) return true;
                if (stem.StartsWith("vesa", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            public static long TryGetSize(string path)
            {
                try { return new FileInfo(path).Length; } catch { return 0; }
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

            // Handle zip / 7z / dosz files
            if (ext.Equals(".zip",  StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".7z",   StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dosz", StringComparison.OrdinalIgnoreCase))
            {
                // .dosz is unambiguously DOSBox Pure content — skip the peek and import as-is.
                if (ext.Equals(".dosz", StringComparison.OrdinalIgnoreCase))
                {
                    await ImportRomFileAsync(romPath, "DOS", fileName);
                    return;
                }

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
                    // Archive contains no recognized ROM extensions.
                    // Try folder name detection before defaulting to Arcade.
                    string fromFolder = RomService.DetectConsoleFromFolderName(romPath);
                    if (!string.IsNullOrEmpty(fromFolder))
                    {
                        ImportLog($"[{fileName}] no recognized ext in archive, folder detection → {fromFolder}");
                        innerConsole = fromFolder;
                    }
                    else
                    {
                        await ImportRomFileAsync(romPath, "Arcade", fileName);
                        return;
                    }
                }

                // Arcade and NeoGeo ROMs are multi-file chip dump archives — import the ZIP as-is.
                // DOS zips are loaded directly by DOSBox Pure (auto-mounts as C:) — also as-is.
                if (innerConsole.Equals("Arcade", StringComparison.OrdinalIgnoreCase) ||
                    innerConsole.Equals("NeoGeo", StringComparison.OrdinalIgnoreCase) ||
                    innerConsole.Equals("DOS",    StringComparison.OrdinalIgnoreCase))
                {
                    await ImportRomFileAsync(romPath, innerConsole, fileName);
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

            if (_knownPaths.Contains(romPath)) { ImportLog($"[{fileName}] SKIPPED — path already in DB"); return; }

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
            _knownPaths.Add(romPath);
            ImportLog($"[{fileName}] INSERTED as {console} (id={game.Id})");
            GameImported?.Invoke(game);

            // Reserve a slot in the artwork counter before firing the background task so the
            // denominator is always >= the numerator even if tasks complete out of order.
            System.Threading.Interlocked.Increment(ref _artworkTotal);

            // Hash and artwork fetch in background — semaphore caps concurrent writers to 6
            // so SQLite isn't locked solid during a large bulk import.
            int taskGen = _drainGeneration;
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

                // ── Discover existing artwork on disk before hitting the network ──
                string? existingCover = _artwork.FindCachedArtwork(hash, console);
                string? existing3D = null;
                string? existingSS = null;

                // Check for BoxArt3D on disk
                string boxArt3DFolder = AppPaths.GetFolder("BoxArt3D", console);
                string boxArt3DPath = Path.Combine(boxArt3DFolder, hash + ".png");
                if (File.Exists(boxArt3DPath)) existing3D = boxArt3DPath;

                // Check for ScreenScraper 2D on disk
                string ss2dFolder = AppPaths.GetFolder("ss2d", console);
                foreach (string ext in new[] { ".png", ".jpg", ".jpeg" })
                {
                    string ssPath = Path.Combine(ss2dFolder, hash + ext);
                    if (File.Exists(ssPath)) { existingSS = ssPath; break; }
                }

                // Apply any discovered artwork to DB immediately
                if (existing3D != null)
                {
                    _db.UpdateBoxArt3D(game.Id, existing3D);
                    game.BoxArt3DPath = existing3D;
                }
                if (existingSS != null)
                {
                    _db.UpdateScreenScraperArt(game.Id, existingSS);
                    game.ScreenScraperArtPath = existingSS;
                }
                if (existingCover != null)
                {
                    _db.UpdateCoverArt(game.Id, existingCover);
                    game.CoverArtPath = existingCover;
                }

                // ── Fetch missing artwork from the network ──
                // Only fetch 2D art (cover + ScreenScraper). 3D art is on-demand only.
                if (existingCover == null)
                {
                    var (artworkPath, ssArtPath, metadata) = await _artwork.FetchArtworkAsync(hash, romPath, console);

                    // Only apply SS art if we didn't already find it on disk
                    if (ssArtPath != null && existingSS == null)
                    {
                        _db.UpdateScreenScraperArt(game.Id, ssArtPath);
                        game.ScreenScraperArtPath = ssArtPath;
                    }

                    if (artworkPath != null)
                    {
                        _db.UpdateCoverArt(game.Id, artworkPath);
                        game.CoverArtPath = artworkPath;

                        if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                            game.Title = metadata.Title;

                        GameImported?.Invoke(game);
                    }
                    else if (ssArtPath != null || existingSS != null)
                    {
                        if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                            game.Title = metadata.Title;
                        GameImported?.Invoke(game);
                    }
                    else
                    {
                        _db.IncrementArtworkAttempts(game.Id);
                    }
                }
                else
                {
                    // Cover was found on disk — still notify UI to refresh the tile
                    GameImported?.Invoke(game);
                }

                // ── Discover existing save states on disk ──
                DiscoverSaveStates(game);

                // Only update progress if this task belongs to the current drain cycle.
                if (taskGen == _drainGeneration)
                {
                    int done  = Interlocked.Increment(ref _artworkDone);
                    int total = _artworkTotal;
                    int pct   = (int)((done / (double)total) * 100);
                    StatusChanged?.Invoke($"Artwork — {pct}%  ({done} of {total})  {game.Title}");
                }
                }
                finally { _hashSemaphore.Release(); }
            });
        }

        /// <summary>
        /// Scans Save States/{Console}/ for subfolders containing .json metadata
        /// whose RomHash matches this game, and re-registers them in the database.
        /// </summary>
        private void DiscoverSaveStates(Game game)
        {
            if (string.IsNullOrEmpty(game.RomHash) || string.IsNullOrEmpty(game.Console)) return;
            try
            {
                string consoleDir = Path.Combine(AppPaths.DataRoot, "Save States",
                    SanitizeFileName(game.Console));
                ImportLog($"[{game.Title}] Looking for save states in: {consoleDir} (hash={game.RomHash})");
                if (!Directory.Exists(consoleDir))
                {
                    ImportLog($"[{game.Title}] Save state dir not found");
                    return;
                }

                int count = 0;
                foreach (string folder in Directory.EnumerateDirectories(consoleDir))
                {
                    foreach (string jsonFile in Directory.EnumerateFiles(folder, "*.json"))
                    {
                        try
                        {
                            string json = File.ReadAllText(jsonFile);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("RomHash", out var hashProp)) continue;
                            string? fileHash = hashProp.GetString();
                            if (!string.Equals(fileHash, game.RomHash, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Found a matching save state — derive file paths from the .json path
                            string stem = Path.GetFileNameWithoutExtension(jsonFile);
                            string dir = Path.GetDirectoryName(jsonFile)!;
                            string statePath = Path.Combine(dir, stem + ".state");
                            string pngPath = Path.Combine(dir, stem + ".png");

                            if (!File.Exists(statePath)) continue;

                            string name = stem;
                            if (root.TryGetProperty("Name", out var nameProp))
                                name = nameProp.GetString() ?? stem;

                            DateTime created = DateTime.Now;
                            if (root.TryGetProperty("CreatedAt", out var dateProp))
                            {
                                string? dateStr = dateProp.GetString();
                                if (dateStr != null && DateTime.TryParse(dateStr, out var parsed))
                                    created = parsed;
                            }

                            string coreName = "";
                            if (root.TryGetProperty("CoreName", out var coreProp))
                                coreName = coreProp.GetString() ?? "";

                            var ss = new SaveState
                            {
                                GameId = game.Id,
                                Name = name,
                                GameTitle = game.Title,
                                ConsoleName = game.Console,
                                CoreName = coreName,
                                RomHash = game.RomHash,
                                StatePath = statePath,
                                ScreenshotPath = File.Exists(pngPath) ? pngPath : "",
                                CreatedAt = created,
                            };
                            _db.InsertSaveState(ss);
                            count++;
                        }
                        catch { /* non-fatal — skip malformed json */ }
                    }
                }
                if (count > 0)
                {
                    game.SaveCount = count;
                    _db.RecalcSaveCount(game.Id);
                    ImportLog($"[{game.Title}] Discovered {count} save state(s) on disk");
                }
            }
            catch (Exception ex)
            {
                ImportLog($"[{game.Title}] Save state discovery error: {ex.Message}");
            }
        }

        private static string SanitizeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
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

                // First pass: look for a non-.bin recognized ROM extension.
                // .bin inside arcade ZIPs are chip dumps, not standalone ROMs —
                // we only treat .bin as ambiguous if the archive has NO other clue.
                bool hasBinOnly = false;
                foreach (var entry in entries)
                {
                    string entryName = entry.Key ?? string.Empty;
                    string ext = Path.GetExtension(entryName);

                    if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        hasBinOnly = true;
                        continue; // skip .bin on first pass — check other entries first
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

                // Archive contains only .bin files and no recognized ROM extensions —
                // this is the typical layout for Arcade chip dumps.  If the folder path
                // hints at a non-Arcade console, honour it; otherwise treat as Arcade.
                if (hasBinOnly)
                {
                    string fromFolder = RomService.DetectConsoleFromFolderName(archivePath);
                    if (!string.IsNullOrEmpty(fromFolder) && !fromFolder.Equals("Arcade", StringComparison.OrdinalIgnoreCase))
                    {
                        ImportLog($"  → .bin-only archive, folder detection → {fromFolder}, returning BIN_AMBIGUOUS");
                        return "BIN_AMBIGUOUS";
                    }
                    ImportLog($"  → .bin-only archive, treating as Arcade");
                    return string.Empty; // routes to Arcade via the caller
                }

                // DOS detection: .exe/.com/.bat inside = DOSBox Pure content.
                // Checked after the recognized-ROM pass so a real ROM takes priority,
                // but before falling back to Arcade.
                bool hasDosExe = entries.Any(e =>
                {
                    string x = Path.GetExtension(e.Key ?? string.Empty);
                    return x.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        || x.Equals(".com", StringComparison.OrdinalIgnoreCase)
                        || x.Equals(".bat", StringComparison.OrdinalIgnoreCase);
                });
                if (hasDosExe)
                {
                    ImportLog($"  → archive contains .exe/.com/.bat, routing to DOS");
                    return "DOS";
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