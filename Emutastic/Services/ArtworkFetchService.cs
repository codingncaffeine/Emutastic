using Emutastic.Models;
using Emutastic.ViewModels;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Orchestrates artwork fetch operations (libretro, ScreenScraper 2D, ScreenScraper 3D).
    /// Extracted from MainWindow code-behind to keep UI layer thin.
    /// </summary>
    public class ArtworkFetchService
    {
        private readonly DatabaseService _db;
        private readonly ArtworkService _artwork;
        private readonly MainViewModel _vm;
        private readonly SynchronizationContext _uiContext;

        /// <summary>Raised when 3D box art is fetched and the UI toggle should become visible.</summary>
        public event Action? BoxArt3DFetched;

        public ArtworkFetchService(DatabaseService db, ArtworkService artwork, MainViewModel vm)
        {
            _db = db;
            _artwork = artwork;
            _vm = vm;
            // Capture the UI SynchronizationContext so background tasks can marshal
            // ObservableCollection modifications back to the UI thread.
            _uiContext = SynchronizationContext.Current
                ?? throw new InvalidOperationException("ArtworkFetchService must be created on the UI thread");
        }

        /// <summary>Posts an action to the UI thread (non-blocking).</summary>
        private void OnUI(Action action) => _uiContext.Post(_ => action(), null);

        /// <summary>
        /// Re-fetches metadata (Developer, Publisher, Genre, Description, Year) for
        /// every game on the given console that currently has any of those fields
        /// empty. Skips games that already have full metadata so re-runs are cheap.
        ///
        /// Triggered by Refresh Library so users who already imported their arcade
        /// library before the arcade-metadata pipeline shipped can fill in the gap
        /// without deleting + re-importing everything. Reports progress via the
        /// optional `progress` callback (called on the UI thread).
        /// </summary>
        public async Task RefreshConsoleMetadataAsync(string console, Action<string>? status = null, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(console)) return;

            // Log to import_debug.log so we can see what this is doing when
            // running from a non-debugger release exe.
            string logPath = Path.Combine(AppPaths.DataRoot, "import_debug.log");
            void Log(string msg)
            {
                try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff}  [RefreshMeta] {msg}\n"); }
                catch { }
            }

            Log($"START console={console}");

            var allConsoleGames = _db.GetAllGames()
                .Where(g => string.Equals(g.Console, console, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Filter: missing fields AND not-yet-attempted. After a game's been
            // through the pipeline once and came back empty (MetadataAttempts >= 1),
            // it gets skipped on auto-resume — some titles genuinely have no
            // entry in any source (regional variants, obscure releases, broken
            // hashes) and retrying every launch is just noise. Manual Refresh
            // Library resets MetadataAttempts to 0 for that console first, so
            // re-trying is a deliberate user action.
            var games = allConsoleGames
                .Where(g => g.MetadataAttempts < 1)
                .Where(g => string.IsNullOrWhiteSpace(g.Developer)
                         || string.IsNullOrWhiteSpace(g.Genre)
                         || string.IsNullOrWhiteSpace(g.Description)
                         || g.Year == 0)
                .ToList();

            Log($"console total={allConsoleGames.Count}, missing-meta-untried={games.Count}");
            if (games.Count == 0) { Log("EXIT — nothing to do"); return; }

            // Determine which network source is the current primary so the
            // status banner reflects it. SS is primary if the user has it
            // configured AND hasn't already burned through their daily quota.
            string SourceLabel()
            {
                var snap = App.Configuration?.GetSnapConfiguration();
                bool ssAvailable = snap is { ScreenScraperEnabled: true }
                                && !string.IsNullOrWhiteSpace(snap.ScreenScraperUser)
                                && !ScreenScraperService.QuotaExhausted;
                return ssAvailable ? "ScreenScraper" : "ArcadeDatabase";
            }

            // Pre-compute the "already done" tail of the status line so users see
            // they're picking up where they left off rather than starting over.
            // missing-meta count is the work LEFT; library total - missing = done.
            int alreadyDone = allConsoleGames.Count - games.Count;
            string resumeSuffix = alreadyDone > 0
                ? $" ({alreadyDone} of {allConsoleGames.Count} already complete)"
                : "";

            bool quotaAnnounced = false;
            void Emit(int done, int total, string? currentTitle = null)
            {
                if (status == null) return;
                bool exhausted = ScreenScraperService.QuotaExhausted;
                string source = SourceLabel();
                int pct = total > 0 ? (done * 100) / total : 0;
                // Truncate long arcade titles so the banner stays one line.
                string titleSuffix = string.IsNullOrEmpty(currentTitle)
                    ? ""
                    : $" — {Truncate(currentTitle!, 60)}";
                string text;
                if (exhausted && !quotaAnnounced)
                {
                    // First iteration after quota transitions to exhausted —
                    // call it out explicitly so the user sees the source switch.
                    text = $"ScreenScraper quota reached — switched to ArcadeDatabase ({done}/{total}, {pct}% {console}{titleSuffix}){resumeSuffix}";
                    quotaAnnounced = true;
                }
                else
                {
                    text = $"Refreshing {console} via {source} — {done + 1}/{total} remaining ({pct}%){titleSuffix}{resumeSuffix}";
                }
                OnUI(() => status(text));
            }

            static string Truncate(string s, int max) =>
                s.Length <= max ? s : s.Substring(0, max - 1) + "…";

            // Initial banner before the first fetch — show the upcoming title
            // so the user sees an immediate, specific signal.
            Emit(0, games.Count, games[0].Title);

            // Parallelize using the user's SS account thread allowance. Free accounts
            // get 1; paid tiers get more (the user's account = 6 at the time of writing).
            // For unauthenticated runs we still parallelize at degree=4 against ADB —
            // ADB advises a single connection per IP but tolerates a small burst,
            // and the rest of the run is bottlenecked by HTTP latency anyway.
            int parallelism = Math.Max(1, ScreenScraperService.CurrentMaxThreads);
            // If SS isn't the active source (no credentials or quota burned), cap
            // parallelism lower so we don't hammer ADB.
            var snap = App.Configuration?.GetSnapConfiguration();
            bool ssActive = snap is { ScreenScraperEnabled: true }
                         && !string.IsNullOrWhiteSpace(snap.ScreenScraperUser)
                         && !ScreenScraperService.QuotaExhausted;
            if (!ssActive) parallelism = Math.Min(parallelism, 4);
            Log($"parallelism={parallelism}, ssActive={ssActive}");

            int done = 0;
            int filledAny = 0;
            try
            {
                await Parallel.ForEachAsync(
                    games,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = cancel,
                    },
                    async (game, ct) =>
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            var (_, _, metadata) = await _artwork.FetchArtworkAsync(
                                game.RomHash ?? "", game.RomPath, game.Console);
                            bool got = metadata != null && (
                                !string.IsNullOrWhiteSpace(metadata.Developer)
                                || !string.IsNullOrWhiteSpace(metadata.Genre)
                                || !string.IsNullOrWhiteSpace(metadata.Description)
                                || !string.IsNullOrWhiteSpace(metadata.ReleaseDate));
                            if (got)
                            {
                                ApplyMetadata(game, metadata);
                                OnUI(() => _vm.RefreshGame(game));
                                Interlocked.Increment(ref filledAny);
                            }
                            // Always mark the game as attempted, regardless of outcome.
                            // If it landed full metadata it falls out of the filter anyway;
                            // if it came back empty we don't want to retry every launch.
                            _db.IncrementMetadataAttempts(game.Id);
                            int captured = Interlocked.Increment(ref done);
                            Log($"[{captured}/{games.Count}] {game.Title} (rom='{Path.GetFileNameWithoutExtension(game.RomPath)}') got-meta={got}");
                            // Emit after each game completes — title shown is the
                            // most recent one to finish (which is more useful than
                            // showing whichever is currently-in-flight when 6 are
                            // running concurrently).
                            Emit(captured, games.Count, game.Title);
                        }
                        catch (Exception ex)
                        {
                            int captured = Interlocked.Increment(ref done);
                            Log($"[{captured}/{games.Count}] {game.Title} EXCEPTION: {ex.Message}");
                            Emit(captured, games.Count, game.Title);
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                Log($"CANCELLED at {done}/{games.Count}, {filledAny} filled");
                if (status != null) OnUI(() => status($"Metadata refresh stopped — {done}/{games.Count} processed. Click Refresh Library again to resume."));
                return;
            }
            Log($"DONE — {filledAny}/{games.Count} games got metadata, quotaExhausted={ScreenScraperService.QuotaExhausted}");
        }

        /// <summary>Persists OpenVGDB metadata from an ArtworkResult onto a Game + DB.</summary>
        private void ApplyMetadata(Game game, ArtworkResult? metadata)
        {
            if (metadata == null) return;
            if (!string.IsNullOrWhiteSpace(metadata.Title))
            {
                game.Title = metadata.Title;
                _db.UpdateTitle(game.Id, metadata.Title);
            }
            if (!string.IsNullOrWhiteSpace(metadata.Developer)
                || !string.IsNullOrWhiteSpace(metadata.Genre))
            {
                game.Developer = metadata.Developer;
                game.Publisher = metadata.Publisher;
                game.Genre = metadata.Genre;
                game.Description = metadata.Description;
                _db.UpdateMetadata(game.Id, metadata.Developer, metadata.Publisher,
                    metadata.Genre, metadata.Description);
            }
            // Year: ReleaseDate may be a bare 4-digit year ("1996") or a full
            // date ("1996-04-19"). Take the first 4 digits if they parse as int.
            // Don't clobber a non-zero year already set on the game (e.g. seeded
            // from the FBNeo DAT during import).
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

        /// <summary>
        /// Retries all games missing cover art on startup. Games whose artwork file
        /// is already on disk get a DB-only repair (instant, no HTTP).
        /// Yields to the UI thread periodically to avoid choking it.
        /// </summary>
        public async Task RetryMissingArtworkAsync()
        {
            // Small delay so the window finishes rendering before we start work.
            await Task.Delay(500);

            var missing = await Task.Run(() => _db.GetGamesWithoutArtwork());
            if (missing.Count == 0) return;

            // Repair pass: fix games whose artwork file is already on disk but the DB
            // path was never saved (e.g. background task killed on last shutdown).
            var stillMissing = new List<Game>();
            var repaired = new List<Game>();
            await Task.Run(() =>
            {
                foreach (var game in missing)
                {
                    string? cached = _artwork.FindCachedArtwork(game.RomHash, game.Console);
                    if (cached != null)
                    {
                        _db.UpdateCoverArt(game.Id, cached);
                        game.CoverArtPath = cached;
                        repaired.Add(game);
                    }
                    else
                    {
                        stillMissing.Add(game);
                    }
                }
            });

            // Batch-refresh repaired games in chunks to avoid flooding the UI thread.
            for (int i = 0; i < repaired.Count; i += 20)
            {
                var chunk = repaired.Skip(i).Take(20).ToList();
                OnUI(() => { foreach (var g in chunk) _vm.RefreshGame(g); });
                await Task.Delay(50); // yield so UI stays responsive
            }

            if (stillMissing.Count == 0) return;
            await FetchArtworkForGamesAsync(stillMissing, "Artwork", silentThreshold: 1);
        }

        /// <summary>
        /// Downloads ScreenScraper 2D art for all games in a specific console that don't have it yet.
        /// </summary>
        public async Task FetchScreenScraperArtForConsoleAsync(string console, string displayName)
        {
            var snapCfg = App.Configuration?.GetSnapConfiguration();
            if (snapCfg == null || !snapCfg.ScreenScraperEnabled
                || string.IsNullOrWhiteSpace(snapCfg.ScreenScraperUser))
                return;

            var allForConsole = await Task.Run(() => _db.GetGamesWithoutScreenScraperArt()
                .Where(g => g.Console == console).ToList());
            if (allForConsole.Count == 0)
            {
                _vm.SetStatus($"{displayName} — all ScreenScraper 2D art already downloaded", autoClear: true);
                return;
            }

            _vm.SetStatus($"{displayName} — fetching ScreenScraper 2D art (0 of {allForConsole.Count})…");
            int done = 0;
            int fetched = 0;
            var ss = new ScreenScraperService();
            var sem = new SemaphoreSlim(1, 1);

            var tasks = allForConsole.Select(async game =>
            {
                await sem.WaitAsync();
                try
                {
                    string? path = await ss.FetchBoxArt2DAsync(
                        snapCfg.ScreenScraperUser, snapCfg.ScreenScraperPassword,
                        game.Console, game.RomHash, game.RomPath);

                    if (path != null)
                    {
                        _db.UpdateScreenScraperArt(game.Id, path);
                        game.ScreenScraperArtPath = path;
                        Interlocked.Increment(ref fetched);
                        OnUI(() => _vm.RefreshGame(game));
                    }

                    int completed = Interlocked.Increment(ref done);
                    if (completed % 10 == 0 || completed == allForConsole.Count)
                        OnUI(() => _vm.SetStatus($"{displayName} — ScreenScraper 2D art ({completed} of {allForConsole.Count})"));
                }
                finally { sem.Release(); }
            });

            await Task.WhenAll(tasks);
            _vm.SetStatus(fetched > 0
                ? $"{displayName} — {fetched} ScreenScraper image{(fetched == 1 ? "" : "s")} downloaded"
                : $"{displayName} — no ScreenScraper artwork found", autoClear: true);
        }

        /// <summary>
        /// Downloads missing libretro cover art for all games in a specific console.
        /// </summary>
        public async Task FetchMissingArtworkForConsoleAsync(string console, string displayName)
        {
            var missing = await Task.Run(() => _db.GetGamesWithoutArtworkForConsole(console));
            if (missing.Count == 0)
            {
                OnUI(() =>
                {
                    _vm.NotificationText = $"{displayName} — all artwork already downloaded";
                    _vm.IsNotification   = true;
                });
                _ = Task.Delay(3000).ContinueWith(_ => OnUI(() =>
                {
                    _vm.IsNotification   = false;
                    _vm.NotificationText = "";
                }));
                return;
            }
            await FetchArtworkForGamesAsync(missing, displayName);
        }

        /// <summary>
        /// Downloads ScreenScraper 3D box art for all games in a specific console.
        /// </summary>
        public async Task Fetch3DBoxArtForConsoleAsync(string console, string displayName)
        {
            var snapConfig = App.Configuration?.GetSnapConfiguration();
            if (snapConfig == null || !snapConfig.ScreenScraperEnabled
                || string.IsNullOrWhiteSpace(snapConfig.ScreenScraperUser))
            {
                _vm.SetStatus("ScreenScraper not configured — set up in Preferences → Snaps", autoClear: true);
                return;
            }

            var games = await Task.Run(() => _db.GetGamesWithout3DBoxArtForConsole(console));
            if (games.Count == 0)
            {
                _vm.SetStatus($"{displayName} — all 3D box art already downloaded", autoClear: true);
                return;
            }

            int total = games.Count;
            int done = 0;
            int fetched = 0;
            int overQuota = 0;

            int ssThreads = Math.Max(1, snapConfig.ScreenScraperMaxThreads);
            _vm.SetStatus($"{displayName} — downloading 3D box art for {total} games…");

            var workers = new System.Collections.Concurrent.ConcurrentQueue<ScreenScraperService>();
            for (int i = 0; i < ssThreads; i++)
                workers.Enqueue(new ScreenScraperService());
            var sem = new SemaphoreSlim(ssThreads, ssThreads);

            var tasks = games.Select(game => Task.Run(async () =>
            {
                if (Interlocked.CompareExchange(ref overQuota, 0, 0) != 0)
                    return;

                await sem.WaitAsync();
                ScreenScraperService worker;
                while (!workers.TryDequeue(out worker!))
                    await Task.Delay(10);
                try
                {
                    if (Interlocked.CompareExchange(ref overQuota, 0, 0) != 0)
                        return;

                    var result = await worker.FetchBoxArt3DAsync(
                        snapConfig.ScreenScraperUser, snapConfig.ScreenScraperPassword,
                        game.Console, game.RomHash, game.RomPath);

                    if (result.OverQuota)
                    {
                        Interlocked.Exchange(ref overQuota, 1);
                        OnUI(() => _vm.SetStatus($"{displayName} — ScreenScraper daily limit reached ({fetched} downloaded)", autoClear: true));
                        return;
                    }

                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                        System.Diagnostics.Debug.WriteLine($"[3D BoxArt] {game.Title}: {result.ErrorMessage}");

                    if (result.LocalPath != null)
                    {
                        _db.UpdateBoxArt3D(game.Id, result.LocalPath);
                        game.BoxArt3DPath = result.LocalPath;
                        Interlocked.Increment(ref fetched);
                        OnUI(() => _vm.RefreshGame(game));
                    }

                    int completed = Interlocked.Increment(ref done);
                    int pct = (int)((completed / (double)total) * 100);
                    OnUI(() => _vm.SetStatus($"{displayName} 3D Box Art — {pct}%  ({completed} of {total})  {game.Title}"));
                }
                finally
                {
                    workers.Enqueue(worker);
                    sem.Release();
                }
            })).ToList();

            await Task.WhenAll(tasks);

            _vm.SetStatus(fetched > 0
                ? $"{displayName} — {fetched} 3D box art image{(fetched == 1 ? "" : "s")} downloaded"
                : $"{displayName} — no 3D box art found on ScreenScraper", autoClear: true);

            if (fetched > 0)
                BoxArt3DFetched?.Invoke();
        }

        /// <summary>
        /// Fetches artwork (libretro + ScreenScraper 2D) for a list of games.
        /// Games with ArtworkAttempts >= silentThreshold produce no status messages.
        /// </summary>
        public async Task FetchArtworkForGamesAsync(List<Game> games, string label,
            int silentThreshold = int.MaxValue)
        {
            var loudGames   = games.Where(g => g.ArtworkAttempts < silentThreshold).ToList();
            var silentGames = games.Where(g => g.ArtworkAttempts >= silentThreshold).ToList();

            int total   = loudGames.Count;
            int done    = 0;
            int fetched = 0;
            var sem     = new SemaphoreSlim(6, 6);

            if (total > 0)
            {
                OnUI(() =>
                {
                    _vm.NotificationText = $"{label} — starting artwork fetch for {total} games…";
                    _vm.IsNotification   = true;
                });
            }

            var loudTasks = loudGames.Select(async game =>
            {
                await sem.WaitAsync();
                try
                {
                    var (artworkPath, ssArtPath, metadata) = await _artwork.FetchArtworkAsync(
                        game.RomHash, game.RomPath, game.Console);

                    if (ssArtPath != null)
                    {
                        _db.UpdateScreenScraperArt(game.Id, ssArtPath);
                        game.ScreenScraperArtPath = ssArtPath;
                    }

                    if (artworkPath != null)
                    {
                        _db.UpdateCoverArt(game.Id, artworkPath);
                        game.CoverArtPath = artworkPath;
                        Interlocked.Increment(ref fetched);
                        ApplyMetadata(game, metadata);
                    }
                    else
                    {
                        _db.IncrementArtworkAttempts(game.Id);
                    }

                    // Even if no artwork found, persist metadata if we got it
                    if (artworkPath == null && metadata != null)
                        ApplyMetadata(game, metadata);

                    if (artworkPath != null || ssArtPath != null)
                        OnUI(() => _vm.RefreshGame(game));

                    int completed = Interlocked.Increment(ref done);
                    int pct = (int)((completed / (double)total) * 100);
                    OnUI(() =>
                    {
                        _vm.NotificationText = $"{label} — {pct}%  ({completed} of {total})  [{game.Console}] {game.Title}";
                        _vm.IsNotification   = true;
                    });
                }
                catch
                {
                    // Count failed fetches toward the attempt cap so we don't
                    // retry the same broken games every launch.
                    try { _db.IncrementArtworkAttempts(game.Id); } catch { }
                }
                finally { sem.Release(); }
            });

            await Task.WhenAll(loudTasks);

            if (total > 0)
            {
                OnUI(() =>
                {
                    _vm.NotificationText = fetched > 0
                        ? $"{label} — {fetched} image{(fetched == 1 ? "" : "s")} downloaded"
                        : $"{label} — no artwork found";
                    _vm.IsNotification   = true;
                });
                _ = Task.Delay(4000).ContinueWith(_ => OnUI(() =>
                {
                    _vm.IsNotification   = false;
                    _vm.NotificationText = "";
                }));
            }

            var silentTasks = silentGames.Select(async game =>
            {
                await sem.WaitAsync();
                try
                {
                    var (artworkPath, ssArtPath, metadata) = await _artwork.FetchArtworkAsync(
                        game.RomHash, game.RomPath, game.Console);

                    if (ssArtPath != null)
                    {
                        _db.UpdateScreenScraperArt(game.Id, ssArtPath);
                        game.ScreenScraperArtPath = ssArtPath;
                    }

                    if (artworkPath != null)
                    {
                        _db.UpdateCoverArt(game.Id, artworkPath);
                        game.CoverArtPath = artworkPath;
                        ApplyMetadata(game, metadata);
                    }
                    else
                    {
                        _db.IncrementArtworkAttempts(game.Id);
                    }

                    if (artworkPath == null && metadata != null)
                        ApplyMetadata(game, metadata);

                    if (artworkPath != null || ssArtPath != null)
                        OnUI(() => _vm.RefreshGame(game));
                }
                catch
                {
                    try { _db.IncrementArtworkAttempts(game.Id); } catch { }
                }
                finally { sem.Release(); }
            });

            await Task.WhenAll(silentTasks);
        }

        /// <summary>
        /// Fetches artwork for a single game (used by context menu "Download Cover Art").
        /// Returns the results for the caller to handle UI-specific actions (dialogs, etc.).
        /// </summary>
        public async Task<(string? artworkPath, string? ssArtPath)> FetchSingleGameArtworkAsync(Game game)
        {
            _vm.SetStatus($"Fetching artwork for {game.Title}…");

            var (artworkPath, ssArtPath, metadata) = await _artwork.FetchArtworkAsync(
                game.RomHash, game.RomPath, game.Console);

            if (ssArtPath != null)
            {
                _db.UpdateScreenScraperArt(game.Id, ssArtPath);
                game.ScreenScraperArtPath = ssArtPath;
            }

            ApplyMetadata(game, metadata);

            if (artworkPath != null)
            {
                _db.UpdateCoverArt(game.Id, artworkPath);
                game.CoverArtPath = artworkPath;

                OnUI(() => _vm.RefreshGame(game));
                _vm.SetStatus("Artwork updated", autoClear: true);
            }
            else if (ssArtPath != null)
            {
                OnUI(() => _vm.RefreshGame(game));
                _vm.SetStatus("Artwork updated (ScreenScraper)", autoClear: true);
            }
            else
            {
                _vm.SetStatus("No artwork found", autoClear: true);
            }

            return (artworkPath, ssArtPath);
        }

        /// <summary>
        /// Backfills metadata (developer, publisher, genre) for all games missing it.
        /// Preloads all OpenVGDB data into memory for fast matching — no per-game queries.
        /// </summary>
        public async Task BackfillMetadataAsync()
        {
            var missing = await Task.Run(() => _db.GetGamesWithoutMetadata());
            if (missing.Count == 0) return;

            string vgdbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "openvgdb.sqlite");
            if (!File.Exists(vgdbPath)) return;

            var updates = new List<(int id, string dev, string pub, string genre, string desc, int year)>();

            await Task.Run(() =>
            {
                // Preload OpenVGDB into memory for fast matching
                var hashToRomId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var filenameToRomId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var releases = new Dictionary<int, (string dev, string pub, string genre, string desc, string date)>();

                using (var vgdb = new SqliteConnection($"Data Source={vgdbPath};Mode=ReadOnly"))
                {
                    vgdb.Open();

                    var romCmd = vgdb.CreateCommand();
                    romCmd.CommandText = "SELECT romID, romHashMD5, romExtensionlessFileName FROM ROMs;";
                    using (var r = romCmd.ExecuteReader())
                        while (r.Read())
                        {
                            int romId = r.GetInt32(0);
                            if (!r.IsDBNull(1)) { string md5 = r.GetString(1); if (md5.Length > 0) hashToRomId.TryAdd(md5, romId); }
                            if (!r.IsDBNull(2)) { string fn = r.GetString(2); if (fn.Length > 0) filenameToRomId.TryAdd(fn, romId); }
                        }

                    var relCmd = vgdb.CreateCommand();
                    relCmd.CommandText = "SELECT romID, releaseDeveloper, releasePublisher, releaseGenre, releaseDescription, releaseDate FROM RELEASES;";
                    using (var r = relCmd.ExecuteReader())
                        while (r.Read())
                        {
                            int romId = r.GetInt32(0);
                            if (releases.ContainsKey(romId)) continue;
                            releases[romId] = (
                                r.IsDBNull(1) ? "" : r.GetString(1),
                                r.IsDBNull(2) ? "" : r.GetString(2),
                                r.IsDBNull(3) ? "" : r.GetString(3),
                                r.IsDBNull(4) ? "" : r.GetString(4),
                                r.IsDBNull(5) ? "" : r.GetString(5));
                        }
                }

                foreach (var game in missing)
                {
                    // Mark every game as attempted up front so misses (no romID match,
                    // or OpenVGDB row with all-empty fields) aren't reconsidered on
                    // subsequent launches. ResetMetadataAttemptsForConsole reopens the
                    // door when the user explicitly retries from the library banner.
                    _db.IncrementMetadataAttempts(game.Id);

                    int romId = -1;

                    if (!string.IsNullOrEmpty(game.RomHash))
                        hashToRomId.TryGetValue(game.RomHash, out romId);

                    if (romId <= 0 && !string.IsNullOrEmpty(game.RomPath))
                    {
                        string fname = Path.GetFileNameWithoutExtension(game.RomPath);
                        if (!filenameToRomId.TryGetValue(fname, out romId) || romId <= 0)
                        {
                            string cleaned = Regex.Replace(fname, @"\(.*?\)|\[.*?\]", "").Trim();
                            foreach (var (key, val) in filenameToRomId)
                            {
                                if (key.StartsWith(cleaned, StringComparison.OrdinalIgnoreCase))
                                { romId = val; break; }
                            }
                        }
                    }

                    if (romId <= 0) continue;
                    if (!releases.TryGetValue(romId, out var rel)) continue;
                    if (string.IsNullOrWhiteSpace(rel.dev) && string.IsNullOrWhiteSpace(rel.pub)
                        && string.IsNullOrWhiteSpace(rel.genre) && string.IsNullOrWhiteSpace(rel.desc)) continue;

                    // Parse year from releaseDate (formats: "1995", "Apr 20, 1995", "December 1995")
                    int year = 0;
                    if (!string.IsNullOrEmpty(rel.date))
                    {
                        var m = Regex.Match(rel.date, @"\b(19|20)\d{2}\b");
                        if (m.Success) year = int.Parse(m.Value);
                    }

                    _db.UpdateMetadata(game.Id, rel.dev, rel.pub, rel.genre, rel.desc);
                    if (year > 0 && game.Year == 0)
                        _db.UpdateYear(game.Id, year);
                    updates.Add((game.Id, rel.dev, rel.pub, rel.genre, rel.desc, year));
                }
            });

            if (updates.Count > 0)
            {
                OnUI(() =>
                {
                    _vm.BulkUpdateMetadata(updates);
                    _vm.SetStatus($"Metadata updated for {updates.Count} games", autoClear: true);
                });
            }
        }
    }
}
