using Emutastic.Services.Archives;
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
        // Each queue item carries the user-selected console nav at the moment of
        // drop. When non-null, that hint coerces the import to that console —
        // sidesteps detection failures (especially for DOS, where filename-only
        // detection is unreliable).
        private readonly Channel<(List<string> Paths, string? HintedConsole)> _importQueue =
            Channel.CreateUnbounded<(List<string>, string?)>(new UnboundedChannelOptions { SingleReader = true });

        // Set per-batch by ProcessImportQueueAsync from the channel item; consulted
        // by single-rom and folder import paths to override console detection.
        private string? _activeHintedConsole;
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
        // Files that the multi-disc bundler decided to fold into an .m3u — both
        // ImportFolderAsync's loops and ImportSingleRomAsync skip these. Cleared
        // between batches. Lives at class level so the same skip set covers files
        // dropped individually and files inside a folder drop in the same batch.
        private readonly HashSet<string> _batchSkipSet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Enqueues paths for import. If an import is already running the new batch
        /// is appended to the queue and progress counters accumulate (OpenEmu-style).
        /// Returns immediately — the actual work happens on a background worker.
        /// </summary>
        public void ImportFilesAsync(IEnumerable<string> filePaths)
            => ImportFilesAsync(filePaths, hintedConsole: null);

        /// <summary>
        /// Variant that takes a user-selected console as a strong hint. When set,
        /// detection is bypassed for the batch — every dropped file/folder is
        /// imported as that console. Use case: user is on the DOS nav and drops
        /// a folder; we trust the nav over fragile filename-based detection.
        /// Pass null when called from "All Games" or any non-console nav.
        /// </summary>
        public void ImportFilesAsync(IEnumerable<string> filePaths, string? hintedConsole)
        {
            var paths = filePaths.ToList();
            if (paths.Count == 0) return;

            // Defense-in-depth: even if a future caller forgets to filter, never
            // let an unknown-console string poison the import. The UI layer
            // already validates via RomService.IsKnownConsoleTag, but if we
            // accept the hint here without re-checking we risk tagging files
            // with nonsense (e.g. a user-collection name) that would never
            // match a console handler later.
            if (!string.IsNullOrEmpty(hintedConsole) && !RomService.IsKnownConsoleTag(hintedConsole))
            {
                System.Diagnostics.Trace.WriteLine($"[Import] Ignoring unknown hinted console '{hintedConsole}'.");
                hintedConsole = null;
            }

            lock (_workerLock)
            {
                _importQueue.Writer.TryWrite((paths, hintedConsole));

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
                if (!_importQueue.Reader.TryRead(out var item))
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

                var paths = item.Paths;
                _activeHintedConsole = item.HintedConsole;
                if (!string.IsNullOrEmpty(_activeHintedConsole))
                    System.Diagnostics.Trace.WriteLine($"[Import] Batch hinted console: {_activeHintedConsole}");

                StatusChanged?.Invoke("Scanning files…");

                // Count new files and add to running total.
                int batchCount = 0;
                await Task.Run(() =>
                {
                    foreach (string path in paths)
                    {
                        if (Directory.Exists(path))
                        {
                            batchCount += Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                                .Count(f => RomService.IsRomFile(f));
                        }
                        else if (File.Exists(path) && RomService.IsRomFile(path))
                            batchCount++;
                    }
                });

                Interlocked.Add(ref _progressTotal, batchCount);
                ProgressChanged?.Invoke(_progressCurrent, _progressTotal);

                // Multi-disc pre-pass: detect (Disc N) / CDN groups across every file
                // in this batch — folders and individual files alike — write .m3u
                // playlists, populate the skip set, drop stale single-disc DB rows.
                // Runs ONCE per batch so we get a single library entry per multi-disc
                // game regardless of whether the user dragged a folder or selected
                // the individual files in Explorer.
                _batchSkipSet.Clear();
                var generatedM3usToImport = await PrepareBatchBundlingAsync(paths);

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

                // Some generated .m3us live in a folder that wasn't explicitly part
                // of the batch (e.g. user dropped individual disc files from Explorer
                // — the generated playlist sits next to them but no folder enum picked
                // it up). Import those directly so the bundle reaches the library.
                foreach (string m3u in generatedM3usToImport)
                {
                    if (_batchSkipSet.Contains(m3u)) continue;
                    if (!File.Exists(m3u)) continue;
                    await ImportSingleRomAsync(m3u);
                }

                _batchSkipSet.Clear();
            }

            ProgressChanged?.Invoke(_progressTotal, _progressTotal);
            IsImporting = false;
            ImportQueueDrained?.Invoke();
        }

        /// <summary>
        /// Batch-level multi-disc detection. Expands every batch path (folders →
        /// recursive file list), runs the bundler, writes .m3u playlists, populates
        /// `_batchSkipSet` with files that should be hidden from the import loop,
        /// removes any pre-existing library rows pointing at those files, and
        /// returns the list of generated .m3u paths so they can be imported even
        /// when the user dropped individual files (no folder enumeration to pick
        /// the .m3u up).
        /// </summary>
        private Task<List<string>> PrepareBatchBundlingAsync(List<string> batchPaths)
        {
            return Task.Run(() =>
            {
                var generated = new List<string>();

                // Gather every candidate ROM file across every batch path.
                var candidates = new List<string>();
                foreach (string p in batchPaths)
                {
                    try
                    {
                        if (Directory.Exists(p))
                            candidates.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories));
                        else if (File.Exists(p))
                            candidates.Add(p);
                    }
                    catch (Exception ex)
                    {
                        ImportLog($"[M3U] WARN scan failed for {p}: {ex.Message}");
                    }
                }
                if (candidates.Count == 0) return generated;

                // Auto-bundle disc-named groups.
                var bundles = M3uBundler.FindBundles(candidates);
                foreach (var bundle in bundles)
                {
                    try
                    {
                        string m3uPath = M3uBundler.WritePlaylist(bundle);
                        generated.Add(m3uPath);
                        if (!candidates.Contains(m3uPath, StringComparer.OrdinalIgnoreCase))
                            candidates.Add(m3uPath);
                        foreach (var disc in bundle.Discs)
                            _batchSkipSet.Add(disc.Path);
                        ImportLog($"[M3U] {bundle.BaseTitle} → {Path.GetFileName(m3uPath)} ({bundle.Discs.Count} discs)");
                    }
                    catch (Exception ex)
                    {
                        ImportLog($"[M3U] WARN failed to write playlist for {bundle.BaseTitle}: {ex.Message}");
                    }
                }

                // Honor every .m3u in the candidate list (auto-generated OR user-
                // authored): skip every disc file the playlist references.
                foreach (string m3u in candidates)
                {
                    if (!Path.GetExtension(m3u).Equals(".m3u", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int skipped = 0;
                    foreach (string discPath in M3uBundler.GetReferencedAbsolutePaths(m3u))
                    {
                        if (_batchSkipSet.Add(discPath)) skipped++;
                    }
                    if (skipped > 0)
                        ImportLog($"[M3U] {Path.GetFileName(m3u)} suppressed {skipped} referenced disc file(s) from import");
                }

                // Widen the skip set to include .bin siblings of every bundled .cue.
                // Without this, a multi-disc set imported as cue/bin pairs leaves
                // the .bin files exposed (the cue gets bundled, the bin's separate
                // "skip-paired-with-cue" check is too late if the user is on a
                // console-nav hint and short-circuits past it).
                var binSiblings = new List<string>();
                foreach (string p in _batchSkipSet)
                {
                    if (!Path.GetExtension(p).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                        continue;
                    binSiblings.AddRange(M3uBundler.GetCueReferencedAbsolutePaths(p));
                }
                int binsAdded = 0;
                foreach (string b in binSiblings)
                {
                    if (_batchSkipSet.Add(b)) binsAdded++;
                }
                if (binsAdded > 0)
                    ImportLog($"[M3U] also suppressing {binsAdded} .bin sidecar(s) referenced by bundled .cue files");

                // Stale-cleanup: drop any pre-existing library row whose RomPath
                // points at a now-bundled disc file, so the user ends up with one
                // entry per multi-disc game even when re-importing a folder that
                // had been imported as separate single-disc rows previously.
                int staleRemoved = 0;
                foreach (string discPath in _batchSkipSet)
                {
                    int? oldId = _db.GetGameIdByRomPath(discPath);
                    if (oldId.HasValue)
                    {
                        try { _db.DeleteGame(oldId.Value); staleRemoved++; }
                        catch (Exception ex)
                        {
                            ImportLog($"[M3U] WARN failed to remove stale entry id={oldId.Value}: {ex.Message}");
                        }
                    }
                }
                if (staleRemoved > 0)
                    ImportLog($"[M3U] removed {staleRemoved} stale single-disc library entr{(staleRemoved == 1 ? "y" : "ies")} replaced by playlist(s)");

                return generated;
            });
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

            // Multi-disc bundling already happened at the batch level
            // (PrepareBatchBundlingAsync). _batchSkipSet contains every disc file
            // that should be skipped, plus generated .m3u paths exist on disk so
            // EnumerateFiles will pick them up below.
            var allFiles = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).ToList();

            // Console-nav hint short-circuit: user dropped a folder while on a
            // specific console nav (e.g. SNES). Trust that signal — recursively
            // flat-import every file as the hinted console, no per-file
            // heuristic detection.
            if (!string.IsNullOrEmpty(_activeHintedConsole)
                && _activeHintedConsole != "All Games")
            {
                foreach (string file in allFiles)
                {
                    if (!RomService.IsRomFile(file)) continue;
                    if (_batchSkipSet.Contains(file)) continue; // skip individual discs that were bundled
                    string pathToImport = await ResolveImportPathAsync(file, _activeHintedConsole);
                    await ImportRomFileAsync(pathToImport, _activeHintedConsole, Path.GetFileName(pathToImport));
                    Interlocked.Increment(ref _progressCurrent);
                    ProgressChanged?.Invoke(_progressCurrent, _progressTotal);
                }
                return;
            }

            // No hint: per-file detection.
            foreach (string file in allFiles)
            {
                if (!RomService.IsRomFile(file)) continue;
                if (_batchSkipSet.Contains(file)) continue; // skip individual discs that were bundled
                await ImportSingleRomAsync(file);
                Interlocked.Increment(ref _progressCurrent);
                ProgressChanged?.Invoke(_progressCurrent, _progressTotal);
            }
        }

        private async Task ImportSingleRomAsync(string romPath)
        {
            string fileName = Path.GetFileName(romPath);
            string ext = Path.GetExtension(romPath);

            // Skip files the batch-level multi-disc bundler folded into a .m3u.
            // The .m3u is the single library entry; the individual disc files
            // shouldn't import as their own games.
            if (_batchSkipSet.Contains(romPath))
            {
                ImportLog($"[Skip] {Path.GetFileName(romPath)} — bundled into .m3u");
                return;
            }

            // .bin paired with a .cue in the same folder — skip it; the .cue is the entry point.
            // Checks for ANY .cue in the folder, not just one with the same base name, so that
            // multi-track dumps (Track 01.bin, Track 02.bin, ...) are correctly skipped when
            // only the .cue shares a different naming pattern.
            //
            // MUST run BEFORE the console-nav hint short-circuit. Otherwise users sitting on
            // PS1/SegaCD/Saturn nav while importing a flat ROM folder end up with the .bin
            // imported as a separate game alongside the .cue (since the hint forces import
            // and the .bin filter never gets a chance to fire).
            if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                string folder = Path.GetDirectoryName(romPath) ?? "";
                if (Directory.EnumerateFiles(folder, "*.cue", SearchOption.TopDirectoryOnly).Any())
                {
                    ImportLog($"[Skip] {Path.GetFileName(romPath)} — paired with .cue in folder");
                    return;
                }
            }

            // Console-nav hint short-circuit: when the user dropped this file
            // while sitting on a specific console nav (e.g. DOS), trust that
            // signal over fragile filename-based detection. Especially valuable
            // for DOS where a bare .exe or generically-named folder otherwise
            // gets misclassified or skipped entirely.
            if (!string.IsNullOrEmpty(_activeHintedConsole) && _activeHintedConsole != "All Games")
            {
                string resolved = await ResolveImportPathAsync(romPath, _activeHintedConsole);
                await ImportRomFileAsync(resolved, _activeHintedConsole, Path.GetFileName(resolved));
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
                if (innerConsole.Equals("Arcade", StringComparison.OrdinalIgnoreCase) ||
                    innerConsole.Equals("NeoGeo", StringComparison.OrdinalIgnoreCase))
                {
                    await ImportRomFileAsync(romPath, innerConsole, fileName);
                    return;
                }

                // Non-arcade archives: extract the single ROM file and re-import it.
                StatusChanged?.Invoke($"Extracting {fileName}…");
                string? extractedPath = await ExtractZipRomAsync(romPath, innerConsole);
                ImportLog($"[{fileName}] extract → {(extractedPath ?? "null (skipped)")}");

                if (extractedPath == null)
                {
                    StatusChanged?.Invoke($"Skipped {fileName} — archive must contain exactly one ROM");
                    return;
                }

                ImportLog($"[{fileName}] RomPathExists={_db.RomPathExists(extractedPath)} → calling ImportRomFileAsync as {innerConsole}");
                // Pass the pre-extraction archive path as originalSourcePath so
                // Refresh Library knows where the user's actual collection lives
                // (the extracted file's parent is under [DataRoot]\ExtractedRoms\
                // which doesn't reflect where the user keeps their zips).
                await ImportRomFileAsync(extractedPath, innerConsole, Path.GetFileName(extractedPath),
                    originalSourcePath: romPath);
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
            string? overrideTitle = null, string? originalSourcePath = null)
        {
            // Capture the user's original selection BEFORE any defensive
            // extraction reassigns romPath. This is what the per-console
            // Refresh Library action keys off — the actual on-disk location
            // of the user's collection, not the post-extraction file.
            string sourcePath = originalSourcePath ?? romPath;

            // ── Defensive extraction guard ──
            // Anything reaching this method that's a .zip/.7z for a non-archive-native
            // console (i.e. anything except Arcade/NeoGeo) gets extracted before being
            // stored. This is belt-and-suspenders on top of the explicit extraction in
            // ImportSingleRomAsync and the hint short-circuits — any future code path
            // that calls this method with an archive path is automatically safe.
            string entryExt = Path.GetExtension(romPath);
            if (ZipRomExtractor.IsArchiveExtension(entryExt)
                && ZipRomExtractor.ConsoleNeedsExtraction(console))
            {
                string? extracted = await ZipRomExtractor.ExtractAsync(romPath, console);
                if (!string.IsNullOrEmpty(extracted) && System.IO.File.Exists(extracted))
                {
                    ImportLog($"[{fileName}] DEFENSIVE EXTRACT {romPath} → {extracted}");
                    romPath = extracted;
                    fileName = Path.GetFileName(extracted);
                }
                else
                {
                    ImportLog($"[{fileName}] WARN — defensive extract failed, importing as-is (may not launch)");
                }
            }

            // ── Copy to library folder if configured ──
            // Portable mode forces a copy into [DataRoot]/Roms/{Console}/ regardless of the
            // user's CopyToLibrary setting — the whole point of portable is that the USB
            // is self-contained, and a ROM living outside PortableData/ defeats that.
            // Logged as a warning when it overrides a user's explicit setting.
            //
            // Source-path-is-ephemeral force-copy: when the user drags a file/folder
            // from inside a still-archived .rar / .7z / .zip viewer (WinRAR, 7-Zip,
            // Windows Explorer's built-in zip browser), the OS-level drag-and-drop
            // hands us a path inside the archiver's temp extraction directory:
            //   WinRAR:  %TEMP%\Rar$DRa{pid}.{n}.rartemp\...
            //   7-Zip:   %TEMP%\7zE{n}.tmp\...
            //   Windows: %TEMP%\Temp{n}_{archive}.zip\...
            // That folder gets garbage-collected the moment the archiver cleans up
            // (close, reboot, periodic sweep), leaving the imported game pointing at
            // a path that no longer exists. Always force-copy out of %TEMP% so the
            // imported game survives the archiver's cleanup, regardless of the user's
            // CopyToLibrary/portable settings.
            bool sourceIsEphemeral = IsUnderSystemTemp(romPath);
            var libConfig = _configService?.GetLibraryConfiguration();
            bool portableForceCopy = AppPaths.IsPortable;
            bool effectiveCopy = portableForceCopy
                              || sourceIsEphemeral
                              || (libConfig is { CopyToLibrary: true } && !string.IsNullOrEmpty(libConfig.LibraryPath));

            if (effectiveCopy)
            {
                try
                {
                    string destDir;
                    if (portableForceCopy)
                    {
                        // Portable wins: route every import into [DataRoot]/Roms/{Console}/.
                        destDir = AppPaths.GetFolder("Roms", console);
                    }
                    else if (libConfig is { CopyToLibrary: true } && !string.IsNullOrEmpty(libConfig.LibraryPath))
                    {
                        destDir = libConfig.LibraryPath;
                        if (libConfig.OrganizeByConsole)
                            destDir = Path.Combine(destDir, console);
                        Directory.CreateDirectory(destDir);
                    }
                    else
                    {
                        // Ephemeral-source fallback: user didn't pick a library
                        // path but we MUST move the file out of %TEMP% before the
                        // archiver cleans up. Use the same DataRoot/Roms/{console}
                        // path portable mode uses.
                        destDir = AppPaths.GetFolder("Roms", console);
                    }

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

                        // For .m3u playlists, copy every disc file the playlist
                        // references (and for .cue discs, the .bin files those reference).
                        // Without this the library copy of the .m3u points at filenames
                        // that exist only in the original folder and the game can't load.
                        if (Path.GetExtension(romPath).Equals(".m3u", StringComparison.OrdinalIgnoreCase))
                            await CopyM3uDiscsAsync(romPath, destDir);

                        romPath  = destPath;
                        fileName = Path.GetFileName(destPath);
                    }
                }
                catch (Exception ex)
                {
                    // In portable mode, falling through to import the source absolute path
                    // would silently break the portable contract — the DB row would point at
                    // a non-portable location that can't follow the USB stick. Skip with a
                    // visible warning instead so the user knows to retry or check permissions.
                    if (portableForceCopy)
                    {
                        ImportLog($"[{fileName}] PORTABLE COPY FAILED — {ex.Message} — skipping");
                        StatusChanged?.Invoke($"Skipped {fileName} — portable copy failed: {ex.Message}");
                        return;
                    }
                    // Ephemeral source: same problem as portable — falling through imports a
                    // path that will vanish when the archiver cleans up. Skip with a warning.
                    if (sourceIsEphemeral)
                    {
                        ImportLog($"[{fileName}] EPHEMERAL COPY FAILED — {ex.Message} — skipping (source under %TEMP%)");
                        StatusChanged?.Invoke($"Skipped {fileName} — extract the archive first, then re-import: {ex.Message}");
                        return;
                    }
                    ImportLog($"[{fileName}] COPY FAILED — {ex.Message}");
                    StatusChanged?.Invoke($"Copy failed for {fileName} — importing in-place");
                    // Non-portable, non-ephemeral: safe to fall through and import from source.
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

            // Arcade (FBNeo): short MAME-style filenames map to full descriptions
            // via the FBNeo DAT (e.g. "mslug" → "Metal Slug - Super Vehicle-001",
            // "kof98" → "The King of Fighters '98 - The Slugfest…"). The DAT also
            // carries year + manufacturer per game, so we pull those at the same
            // time and seed Year/Developer fields on the Game before insert —
            // detail card shows them immediately, no waiting on the network. Genre
            // and description require the network metadata pass below (SS or ADB).
            //
            // Requires the user to have downloaded the Arcade (FBNeo) DAT via
            // Preferences → Cores / Extras.
            int seededYear = 0;
            string seededDeveloper = "";
            if (console == "Arcade" && overrideTitle == null)
            {
                string romName = Path.GetFileNameWithoutExtension(romPath);
                var datMeta = _datMatcher.LookupArcadeMeta(romName);
                if (datMeta != null)
                {
                    title = datMeta.Title;
                    if (!string.IsNullOrWhiteSpace(datMeta.Year) && int.TryParse(datMeta.Year, out int y))
                        seededYear = y;
                    if (!string.IsNullOrWhiteSpace(datMeta.Manufacturer))
                        seededDeveloper = datMeta.Manufacturer;
                }
            }

            var colors = RomService.GetConsoleColors(console);

            var game = new Game
            {
                Title = title,
                Console = console,
                Manufacturer = manufacturer,
                RomPath = romPath,
                OriginalSourcePath = sourcePath,
                RomHash = string.Empty,
                BackgroundColor = colors.bg,
                AccentColor = colors.accent,
                Year = seededYear,
                Developer = seededDeveloper,
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
                        PersistMetadataFields(game, metadata);

                        GameImported?.Invoke(game);
                    }
                    else if (ssArtPath != null || existingSS != null)
                    {
                        if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                            game.Title = metadata.Title;
                        PersistMetadataFields(game, metadata);
                        GameImported?.Invoke(game);
                    }
                    else
                    {
                        // No artwork but ArtworkResult may still carry SS/ADB
                        // arcade metadata — persist those fields before falling
                        // through to the attempt counter. Otherwise arcade
                        // imports without cover art lose their Genre / Description.
                        PersistMetadataFields(game, metadata);
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
                using var archive = RomArchive.Open(archivePath);
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

        /// <summary>
        /// Persists Developer/Publisher/Genre/Description (and Year via ReleaseDate)
        /// from an ArtworkResult onto the Game + DB. Mirrors
        /// ArtworkFetchService.ApplyMetadata so fresh imports (which go through
        /// ImportRomFileAsync, not ArtworkFetchService) actually persist the
        /// fields the network sources returned, not just the title.
        /// </summary>
        private void PersistMetadataFields(Game game, ArtworkResult? metadata)
        {
            if (metadata == null) return;
            if (!string.IsNullOrWhiteSpace(metadata.Developer)
                || !string.IsNullOrWhiteSpace(metadata.Genre))
            {
                game.Developer   = metadata.Developer;
                game.Publisher   = metadata.Publisher;
                game.Genre       = metadata.Genre;
                game.Description = metadata.Description;
                _db.UpdateMetadata(game.Id, metadata.Developer, metadata.Publisher,
                    metadata.Genre, metadata.Description);
            }
            if (!string.IsNullOrWhiteSpace(metadata.ReleaseDate) && game.Year == 0)
            {
                string head = metadata.ReleaseDate.Length >= 4 ? metadata.ReleaseDate[..4] : metadata.ReleaseDate;
                if (int.TryParse(head, out int year) && year >= 1970 && year <= 2100)
                {
                    game.Year = year;
                    _db.UpdateYear(game.Id, year);
                }
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

        /// <summary>
        /// When the import flow uses the console-nav hint short-circuit, the file path
        /// goes straight to the DB without per-file detection. That bypasses the zip
        /// extract step in ImportSingleRomAsync. This helper restores that step: if
        /// the path is an archive and the hinted console isn't archive-native, extract
        /// to ExtractedRoms and return the inner ROM path. Otherwise returns the input.
        /// </summary>
        private async Task<string> ResolveImportPathAsync(string romPath, string hintedConsole)
        {
            string ext = Path.GetExtension(romPath);
            if (!ZipRomExtractor.IsArchiveExtension(ext)) return romPath;
            if (!ZipRomExtractor.ConsoleNeedsExtraction(hintedConsole)) return romPath;
            string? extracted = await ZipRomExtractor.ExtractAsync(romPath, hintedConsole);
            return extracted ?? romPath;
        }

        private async Task<string?> ExtractZipRomAsync(string archivePath, string console)
        {
            try
            {
                // Extract under DataRoot, NOT %TEMP% — Windows wipes TEMP periodically and
                // the extracted path was being stored as the game's RomPath, leading to
                // "ROM file not found" the next time the user launched the game.
                string outputDir = AppPaths.GetFolder("ExtractedRoms", console);

                using var archive = RomArchive.Open(archivePath);

                var romEntries = new List<IRomArchiveEntry>();
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    string ext = Path.GetExtension(entry.Key ?? string.Empty);
                    if (RomService.IsRomExtension(ext))
                        romEntries.Add(entry);
                }

                if (romEntries.Count != 1) return null;

                var romEntry = romEntries[0];
                string outputPath = Path.Combine(outputDir, Path.GetFileName(romEntry.Key!));
                string tmpPath    = outputPath + ".tmp";

                // Reuse only if the existing file has a sane non-zero size that matches.
                // Some archive formats report Size <= 0 — skip fast-path and re-extract then.
                if (romEntry.Size > 0
                    && File.Exists(outputPath)
                    && new FileInfo(outputPath).Length == romEntry.Size)
                    return outputPath;

                // Write to .tmp first so a partial extraction (disk full, IO error, app crash)
                // never leaves a half-written file that the size-match path could later reuse.
                if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }

                // Stream directly to disk — avoids buffering large ISO entries
                // (PS1/GC/Wii images, 700MB+) in memory.
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
                StatusChanged?.Invoke($"Extraction failed for {Path.GetFileName(archivePath)}: {ex.Message}");
                return null;
            }
        }

        // ── Copy-to-library helpers ───────────────────────────────────────────

        private async Task CopyFileAsync(string source, string dest)
        {
            const int bufferSize = 81920; // 80 KB — good balance for HDD/SSD
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize, useAsync: true);
            using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize, useAsync: true);

            // For small files (<8 MB) just copy — emitting per-byte progress for
            // tiny ROMs is wasteful and can flood the UI thread. Larger files
            // (PSP ISOs, GC/Wii images) report progress every ~500 ms so the
            // status doesn't sit frozen on a single per-file message for minutes.
            long total = src.Length;
            if (total < 8 * 1024 * 1024)
            {
                await src.CopyToAsync(dst);
                return;
            }

            string fileName = Path.GetFileName(source);
            string totalMb = (total / 1048576d).ToString("F0");
            byte[] buffer = new byte[bufferSize];
            long copied = 0;
            var lastUpdate = Environment.TickCount64;

            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, bufferSize))) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                copied += read;

                long now = Environment.TickCount64;
                if (now - lastUpdate >= 500)
                {
                    int pct = (int)((copied * 100L) / total);
                    string copiedMb = (copied / 1048576d).ToString("F0");
                    StatusChanged?.Invoke($"Copying {fileName}… {pct}% ({copiedMb} / {totalMb} MB)");
                    lastUpdate = now;
                }
            }
        }

        /// <summary>
        /// Parses an .m3u playlist and copies every referenced disc file (.cue/.chd/
        /// .iso/.gdi/etc.) into destDir. For .cue entries, also copies the .bin files
        /// those reference. Required when CopyToLibrary is on — otherwise the library
        /// copy of the .m3u points at sibling filenames that don't exist there.
        /// </summary>
        private async Task CopyM3uDiscsAsync(string m3uPath, string destDir)
        {
            string? m3uDir = Path.GetDirectoryName(m3uPath);
            if (m3uDir == null) return;

            foreach (string rawLine in File.ReadAllLines(m3uPath))
            {
                string entry = rawLine.Trim();
                if (string.IsNullOrEmpty(entry) || entry.StartsWith("#")) continue;
                // Resolve relative to the m3u's directory.
                string discSrc = Path.IsPathRooted(entry) ? entry : Path.Combine(m3uDir, entry);
                if (!File.Exists(discSrc)) continue;

                string discName = Path.GetFileName(discSrc);
                string discDest = Path.Combine(destDir, discName);
                if (!File.Exists(discDest))
                {
                    StatusChanged?.Invoke($"Copying {discName}…");
                    await CopyFileAsync(discSrc, discDest);
                }

                // Cue entries pull their .bin tracks too.
                if (Path.GetExtension(discSrc).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                    await CopyCueBinsAsync(discSrc, destDir);
            }
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
        /// True when the path is under the OS temp directory — i.e. came from a
        /// drag-and-drop out of WinRAR / 7-Zip / Windows zip browser, where the
        /// archiver extracted to its own temp folder before the OS handed us the
        /// path. Such paths are deleted by the archiver's cleanup, so the import
        /// MUST copy the file to a permanent location before storing the path
        /// in the DB.
        /// </summary>
        private static bool IsUnderSystemTemp(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                return full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || full.Equals(temp, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
