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
                _vm.SetStatus($"{displayName} — all artwork already downloaded", autoClear: true);
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
                _vm.SetStatus($"{label} — starting artwork fetch for {total} games…");

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
                    else if (ssArtPath == null)
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
                    OnUI(() => _vm.SetStatus($"{label} — {pct}%  ({completed} of {total})  {game.Title}"));
                }
                finally { sem.Release(); }
            });

            await Task.WhenAll(loudTasks);

            if (total > 0)
            {
                _vm.SetStatus(fetched > 0
                    ? $"{label} — {fetched} image{(fetched == 1 ? "" : "s")} downloaded"
                    : $"{label} — no artwork found", autoClear: true);
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
                    else if (ssArtPath == null)
                    {
                        _db.IncrementArtworkAttempts(game.Id);
                    }

                    if (artworkPath == null && metadata != null)
                        ApplyMetadata(game, metadata);

                    if (artworkPath != null || ssArtPath != null)
                        OnUI(() => _vm.RefreshGame(game));
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
                    // Skip if OpenVGDB has no useful metadata — writing empty strings
                    // would leave Developer = '' which still matches "missing" on next launch
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
