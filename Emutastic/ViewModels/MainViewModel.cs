using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emutastic.Converters;
using Emutastic.Models;
using Emutastic.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly DatabaseService _db;
        private ObservableCollection<Game> _allGames = new();
        // O(1) lookup by Game.Id — rebuilt on Reload(), updated in RefreshGame.
        private Dictionary<int, Game> _gameIndex = new();

        private ObservableCollection<Game> _games = new();
        public ObservableCollection<Game> Games
        {
            get => _games;
            set
            {
                // Skip PropertyChanged (and the expensive WPF rebind) if it's the same collection.
                if (ReferenceEquals(_games, value)) return;
                _games = value;
                OnPropertyChanged();
            }
        }

        [ObservableProperty]
        private string _selectedConsole = "All Games";

        [ObservableProperty]
        private bool _isMixedView = true;

        // Cached filtered results — reused across console-switch round trips.
        // Invalidated whenever games are added, removed, or reloaded.
        private readonly ConcurrentDictionary<string, ObservableCollection<Game>> _consoleCache = new();
        private volatile bool _filterDirty = true;

        // Rapid sidebar clicks queue independent Task.Run sorts; the cancellation
        // token here gates the final assignment so only the latest click wins.
        // The in-flight sort runs to completion — wasted CPU is fine, a wrong-
        // console flash on screen is not.
        private System.Threading.CancellationTokenSource? _filterCts;

        [ObservableProperty]
        private string _gameCountText = "";

        [ObservableProperty]
        private string _statusText = "";

        // Drives the bottom-left transient banner. Either an import is in progress
        // (with a progress bar) or a notification is being surfaced (text only,
        // e.g. "core updates available"). The banner is visible while either
        // flag is true; when both, the import message takes precedence.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
        [NotifyPropertyChangedFor(nameof(IsProgressBarVisible))]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        [NotifyPropertyChangedFor(nameof(BannerProgressPercent))]
        private bool _isImporting;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        private string _importStatusText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerProgressPercent))]
        private double _importProgressPercent;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        private bool _isNotification;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        private string _notificationText = "";

        // Surfaced from the Cores preferences "Update All" flow so the user
        // sees per-completion progress + failure summary in the same banner.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
        [NotifyPropertyChangedFor(nameof(IsProgressBarVisible))]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        [NotifyPropertyChangedFor(nameof(BannerProgressPercent))]
        private bool _isCoreUpdating;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        private string _coreUpdateText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerProgressPercent))]
        private double _coreUpdateProgressPercent;

        // Surfaced from the manual-download flow (context menu / detail card /
        // in-game cog) so the user sees streaming progress + failures in the banner.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
        [NotifyPropertyChangedFor(nameof(IsProgressBarVisible))]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        [NotifyPropertyChangedFor(nameof(BannerProgressPercent))]
        private bool _isDownloadingManual;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerText))]
        private string _manualDownloadText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BannerProgressPercent))]
        private double _manualDownloadProgressPercent;

        public bool IsBannerVisible => IsImporting || IsCoreUpdating || IsNotification || IsDownloadingManual;
        public bool IsProgressBarVisible => IsImporting || IsCoreUpdating || IsDownloadingManual;

        // Priority: import > core-update > manual-download > notification (most-active-task wins).
        public string BannerText =>
            IsImporting         ? ImportStatusText :
            IsCoreUpdating      ? CoreUpdateText :
            IsDownloadingManual ? ManualDownloadText :
                                  NotificationText;

        public double BannerProgressPercent =>
            IsImporting         ? ImportProgressPercent :
            IsCoreUpdating      ? CoreUpdateProgressPercent :
            IsDownloadingManual ? ManualDownloadProgressPercent :
                                  0;

        private ObservableCollection<ConsoleGroup> _groupedGames = new();
        public ObservableCollection<ConsoleGroup> GroupedGames
        {
            get => _groupedGames;
            set
            {
                if (ReferenceEquals(_groupedGames, value)) return;
                _groupedGames = value;
                OnPropertyChanged();
            }
        }

        [ObservableProperty]
        private bool _isGroupedView;

        [ObservableProperty]
        private string _toolbarTitle = "";

        [ObservableProperty]
        private bool _isShowingFavorites;

        /// <summary>Raised after any navigation command completes. Arg is the console tag or category name.</summary>
        public event Action<string>? Navigated;

        public IAsyncRelayCommand<string> NavigateToConsoleCommand { get; }
        public IAsyncRelayCommand NavigateToAllGamesCommand { get; }
        public IAsyncRelayCommand NavigateToRecentCommand { get; }
        public IAsyncRelayCommand NavigateToFavoritesCommand { get; }
        public IAsyncRelayCommand NavigateToRecentlyAddedCommand { get; }
        public IAsyncRelayCommand<int> NavigateToCollectionCommand { get; }

        public MainViewModel(DatabaseService db)
        {
            _db = db;

            NavigateToConsoleCommand = new AsyncRelayCommand<string>(NavigateToConsoleAsync);
            NavigateToAllGamesCommand = new AsyncRelayCommand(NavigateToAllGamesAsync);
            NavigateToRecentCommand = new AsyncRelayCommand(NavigateToRecentAsync);
            NavigateToFavoritesCommand = new AsyncRelayCommand(NavigateToFavoritesAsync);
            NavigateToRecentlyAddedCommand = new AsyncRelayCommand(NavigateToRecentlyAddedAsync);
            NavigateToCollectionCommand = new AsyncRelayCommand<int>(NavigateToCollectionAsync);
        }

        private async Task PreloadVisibleArtworkAsync()
        {
            var visible = Games.Take(40).ToList();
            int uncached = visible.Count(g =>
                !Converters.PathToImageConverter.IsCached(g.DisplayArtPath));
            if (uncached > 5)
            {
                SetStatus("Loading artwork…");
                var paths = visible.Select(g => g.DisplayArtPath);
                await Converters.PathToImageConverter.PreloadAsync(paths);
                SetStatus("Loading artwork…", autoClear: true);
            }
        }

        private async Task NavigateToConsoleAsync(string? tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            IsShowingFavorites = false;
            IsMixedView = false;
            SelectedConsole = tag;
            await FilterGamesAsync();
            Navigated?.Invoke(tag);
            await PreloadVisibleArtworkAsync();
        }

        private async Task NavigateToAllGamesAsync()
        {
            IsShowingFavorites = false;
            IsMixedView = true;
            SelectedConsole = "All Games";
            await FilterGamesAsync();
            ToolbarTitle = "All Games";
            Navigated?.Invoke("All Games");
            await PreloadVisibleArtworkAsync();
        }

        private async Task NavigateToRecentAsync()
        {
            IsShowingFavorites = false;
            IsMixedView = true;
            LoadRecent(_db);
            ToolbarTitle = "Recently Played";
            Navigated?.Invoke("Recent");
            await PreloadVisibleArtworkAsync();
        }

        private async Task NavigateToFavoritesAsync()
        {
            IsShowingFavorites = true;
            IsMixedView = true;
            LoadFavorites(_db);
            ToolbarTitle = "Favorites";
            Navigated?.Invoke("Favorites");
            await PreloadVisibleArtworkAsync();
        }

        private async Task NavigateToRecentlyAddedAsync()
        {
            CancelInFlightSearch();
            IsShowingFavorites = false;
            IsMixedView = true;
            var games = _db.GetRecentlyAdded(25);
            Games = new ObservableCollection<Game>(games);
            IsGroupedView = false;
            GameCountText = $"{games.Count} games";
            ToolbarTitle = "Recently Added";
            Navigated?.Invoke("RecentlyAdded");
            await PreloadVisibleArtworkAsync();
        }

        private async Task NavigateToCollectionAsync(int collectionId)
        {
            CancelInFlightSearch();
            IsShowingFavorites = false;
            IsMixedView = true;
            var games = _db.GetGamesByCollectionId(collectionId);
            Games = new ObservableCollection<Game>(games);
            IsGroupedView = false;
            GameCountText = $"{games.Count} games";
            Navigated?.Invoke($"Collection:{collectionId}");
            await PreloadVisibleArtworkAsync();
        }

        public void Reload()
        {
            var games = _db.GetAllGames();
            _allGames = new ObservableCollection<Game>(games);
            _gameIndex = games.ToDictionary(g => g.Id);
            InvalidateCache();
        }

        public void RefreshGame(Game updated)
        {
            // Merge non-default fields from 'updated' onto the existing game so partial
            // objects (e.g. from the missing-artwork query) don't wipe fields like
            // BoxArt3DPath, PlayCount, IsFavorite, etc.
            void MergeOnto(Game target)
            {
                if (!string.IsNullOrEmpty(updated.Title))     target.Title = updated.Title;
                if (!string.IsNullOrEmpty(updated.CoverArtPath))
                {
                    PathToImageConverter.Evict(target.CoverArtPath);
                    target.CoverArtPath = updated.CoverArtPath;
                }
                if (!string.IsNullOrEmpty(updated.BoxArt3DPath))
                {
                    PathToImageConverter.Evict(target.BoxArt3DPath);
                    target.BoxArt3DPath = updated.BoxArt3DPath;
                }
                if (!string.IsNullOrEmpty(updated.ScreenScraperArtPath))
                {
                    PathToImageConverter.Evict(target.ScreenScraperArtPath);
                    target.ScreenScraperArtPath = updated.ScreenScraperArtPath;
                }
                if (!string.IsNullOrEmpty(updated.Developer))  target.Developer = updated.Developer;
                if (!string.IsNullOrEmpty(updated.Publisher))  target.Publisher = updated.Publisher;
                if (!string.IsNullOrEmpty(updated.Genre))      target.Genre = updated.Genre;
                if (!string.IsNullOrEmpty(updated.Description)) target.Description = updated.Description;
                if (!string.IsNullOrEmpty(updated.RomHash))   target.RomHash = updated.RomHash;
                if (!string.IsNullOrEmpty(updated.RomPath))   target.RomPath = updated.RomPath;
                if (updated.BackgroundColor != "#1F1F21")     target.BackgroundColor = updated.BackgroundColor;
                if (updated.AccentColor != "#E03535")          target.AccentColor = updated.AccentColor;
                if (updated.PlayCount > 0)   target.PlayCount = updated.PlayCount;
                if (updated.SaveCount > 0)   target.SaveCount = updated.SaveCount;
                if (updated.TotalPlayTimeSeconds > 0) target.TotalPlayTimeSeconds = updated.TotalPlayTimeSeconds;
                if (updated.IsFavorite)      target.IsFavorite = true;
                if (updated.Rating > 0)      target.Rating = updated.Rating;
                if (updated.LastPlayed != null) target.LastPlayed = updated.LastPlayed;
                if (!string.IsNullOrEmpty(updated.ManualPath)) target.ManualPath = updated.ManualPath;
                if (!string.IsNullOrEmpty(updated.Notes))      target.Notes = updated.Notes;
                if (!string.IsNullOrEmpty(updated.PatchPath))  target.PatchPath = updated.PatchPath;
            }

            // O(1) lookup via index instead of linear scan
            _gameIndex.TryGetValue(updated.Id, out var existing);

            if (existing != null)
            {
                MergeOnto(existing);
            }
            else
            {
                _allGames.Add(updated);
                _gameIndex[updated.Id] = updated;
            }

            // The cached search index now lags reality: this call either added a
            // game it has never seen or rewrote fields it indexed (Title,
            // Developer, …). Unlike the console caches below (updated in place,
            // by design), the search index is a flat snapshot — drop it and let
            // the next unscoped search rebuild. Without this, games imported or
            // renamed mid-session are silently invisible to search in the
            // All Games / Favorites / Recently Played views until restart.
            _searchIndex = null;

            var target = existing ?? updated;
            string console = target.Console ?? "";

            // Update caches silently (no re-seat) — these are only used when switching consoles,
            // so we just need the data to be correct, not trigger WPF layout.
            if (!string.IsNullOrEmpty(console) && _consoleCache.TryGetValue(console, out var cached))
            {
                if (!cached.Any(g => g.Id == target.Id))
                    cached.Add(target);
            }
            if (_consoleCache.TryGetValue("All Games", out var allCache))
            {
                if (!allCache.Any(g => g.Id == target.Id))
                    allCache.Add(target);
            }

            // Only re-seat the currently visible Games collection (triggers WPF render).
            var inView = Games.FirstOrDefault(g => g.Id == updated.Id);
            if (inView != null)
            {
                if (inView != existing) MergeOnto(inView);
                int idx = Games.IndexOf(inView);
                Games[idx] = inView; // re-seat to trigger collection change
            }
            else if (Games != _allGames)
            {
                // Game not in current view — add it if we're viewing its console or "All Games"
                string viewing = SelectedConsole ?? "";
                if (viewing == "All Games" || viewing == console)
                    Games.Add(target);
            }
        }

        public void RefreshAllGames()
        {
            // Reassign to a new collection so the property-change fires and all bindings refresh.
            Games = new ObservableCollection<Game>(Games);
        }

        /// <summary>
        /// Updates metadata fields on the in-memory Game object without re-seating
        /// in the collection (metadata isn't shown in the grid, only in the detail card).
        /// </summary>
        public void UpdateGameMetadata(int gameId, string developer, string publisher, string genre, string description)
        {
            var game = _allGames.FirstOrDefault(g => g.Id == gameId);
            if (game == null) return;
            game.Developer = developer;
            game.Publisher = publisher;
            game.Genre = genre;
            game.Description = description;
        }

        public void UpdateGameYear(int gameId, int year)
        {
            var game = _allGames.FirstOrDefault(g => g.Id == gameId);
            if (game != null && game.Year == 0)
                game.Year = year;
        }

        /// <summary>
        /// Batch-updates metadata for many games using an O(1) lookup instead of
        /// repeated FirstOrDefault scans. Prevents UI-thread stalls on large libraries.
        /// </summary>
        public void BulkUpdateMetadata(List<(int id, string dev, string pub, string genre, string desc, int year)> updates)
        {
            var lookup = _allGames.ToDictionary(g => g.Id);
            foreach (var (id, dev, pub, genre, desc, year) in updates)
            {
                if (!lookup.TryGetValue(id, out var game)) continue;
                game.Developer = dev;
                game.Publisher = pub;
                game.Genre = genre;
                game.Description = desc;
                if (year > 0 && game.Year == 0)
                    game.Year = year;
            }
        }

        public void RemoveGame(Game game)
        {
            var inAll = _allGames.FirstOrDefault(g => g.Id == game.Id);
            var inView = Games.FirstOrDefault(g => g.Id == game.Id);
            if (inAll != null) _allGames.Remove(inAll);
            if (inView != null) Games.Remove(inView);
            InvalidateCache();
            UpdateCount();
        }

        public async Task FilterGamesAsync()
        {
            CancelInFlightSearch();
            var console = SelectedConsole;

            // Cache hit — reuse the previously built collection for this console.
            if (!_filterDirty && _consoleCache.TryGetValue(console, out var cached))
            {
                _filterCts?.Cancel(); // a stale slow-path may still be running; suppress its commit
                Games = cached;
                IsGroupedView = false;
                UpdateCount();
                return;
            }

            _filterCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _filterCts = cts;
            var token = cts.Token;

            List<Game> result = null!;
            await Task.Run(() =>
            {
                result = console == "All Games"
                    ? _allGames.OrderBy(g => g.Console).ThenBy(g => g.Title).ToList()
                    : _allGames.Where(g => g.Console == console).OrderBy(g => g.Title).ToList();
            }, token);

            if (token.IsCancellationRequested) return;

            var oc = new ObservableCollection<Game>(result);
            _consoleCache[console] = oc;
            if (console == "All Games")
                _filterDirty = false;

            Games         = oc;
            IsGroupedView = false;
            UpdateCount();
        }

        public void LoadFavorites(DatabaseService db)
        {
            CancelInFlightSearch();
            var favs = db.GetFavorites();
            Games = new ObservableCollection<Game>(favs);
            IsGroupedView = false;
            InvalidateCache();
            UpdateCount();
        }

        public void LoadRecent(DatabaseService db)
        {
            CancelInFlightSearch();
            var recent = db.GetRecentlyPlayed();
            Games = new ObservableCollection<Game>(recent);
            IsGroupedView = false;
            InvalidateCache();
            UpdateCount();
        }

        // Search debounce: cancel the previous in-flight search whenever a new
        // keystroke comes in so we only do one pass after the user pauses typing.
        // Avoids hammering the LINQ filter on a multi-thousand-game library and
        // prevents results flicker as the user types out a longer query.
        private System.Threading.CancellationTokenSource? _searchCts;

        // Any navigation that replaces Games must also kill an in-flight search:
        // OnNavigated clears the search box with TextChanged suppressed, so no
        // SearchGames("") arrives to cancel it, and the stale result set would
        // otherwise land on top of the freshly navigated view ~200ms later.
        private void CancelInFlightSearch() => _searchCts?.Cancel();

        // Pre-computed lowercased searchable text per game (gameId → text).
        // Concatenates Title + Console + Developer + Publisher + Genre + Year
        // so search hits all of them in a single substring scan. Built lazily
        // on first search and invalidated on every library mutation:
        // Reload / RemoveGame go through InvalidateCache; RefreshGame
        // nulls _searchIndex directly (it deliberately keeps the console caches
        // alive, so it must not call InvalidateCache).
        // volatile so a concurrent invalidate is seen by the next search pass
        // without a lock.
        private volatile Dictionary<int, string>? _searchIndex;

        /// <summary>
        /// Tokenized substring search across Title + Console + Developer +
        /// Publisher + Genre + Year. Tasks cancel on every new keystroke;
        /// the underlying collection is snapshotted on the UI thread BEFORE
        /// the background pass to avoid racing a concurrent import that
        /// mutates ObservableCollection&lt;Game&gt; without a lock.
        ///
        /// Returns a Task so callers can observe completion / exceptions
        /// (event handler should fire-and-forget with a logging continuation
        /// — async void on a VM method swallows exceptions to the
        /// SynchronizationContext and crashes the app).
        /// </summary>
        public async Task SearchGames(string query, string? scopeConsole = null)
        {
            _searchCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _searchCts = cts;
            var token = cts.Token;

            if (string.IsNullOrWhiteSpace(query))
            {
                await FilterGamesAsync();
                return;
            }

            _ = Task.Delay(400, token).ContinueWith(_ =>
            {
                if (!token.IsCancellationRequested)
                    GameCountText = "Searching…";
            }, token, TaskContinuationOptions.OnlyOnRanToCompletion,
               TaskScheduler.FromCurrentSynchronizationContext());

            try { await Task.Delay(180, token); }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested) return;

            var tokens = query
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeForSearch)
                .Where(t => t.Length > 0)
                .ToArray();
            if (tokens.Length == 0)
            {
                await FilterGamesAsync();
                return;
            }

            bool scoped = !string.IsNullOrEmpty(scopeConsole);
            var snapshot = scoped
                ? _allGames.Where(g => g.Console == scopeConsole).ToArray()
                : _allGames.ToArray();

            List<Game>? filtered = null;
            try
            {
                await Task.Run(() =>
                {
                    var index = scoped
                        ? BuildSearchIndex(snapshot)
                        : GetOrBuildSearchIndex(snapshot);
                    filtered = new List<Game>(snapshot.Length);
                    foreach (var g in snapshot)
                    {
                        if (g == null) continue;
                        // Self-healing: a game missing from the cached index
                        // (added after the index was built) must never be
                        // silently unsearchable — compute its text inline.
                        if (!index.TryGetValue(g.Id, out var text))
                            text = BuildSearchableText(g);
                        bool all = true;
                        foreach (var t in tokens)
                        {
                            if (!text.Contains(t, StringComparison.Ordinal))
                            {
                                all = false;
                                break;
                            }
                        }
                        if (all) filtered.Add(g);
                    }
                }, token);
            }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested || filtered == null) return;

            Games = new ObservableCollection<Game>(filtered);
            IsGroupedView = false;
            GameCountText = filtered.Count == 1 ? "1 result" : $"{filtered.Count} results";
            // The 400ms "Searching…" continuation is still pending whenever the
            // search finishes faster than that (the common case). Cancel it so
            // it can't overwrite the final result count. Safe: every later
            // consumer of this CTS only ever calls Cancel() again.
            cts.Cancel();
        }

        /// <summary>
        /// Returns the current search index, rebuilding it from the supplied
        /// snapshot if it's been invalidated. Idempotent under concurrent
        /// callers — worst case two passes both rebuild and the second write
        /// wins (same data, no consistency issue).
        /// </summary>
        private static Dictionary<int, string> BuildSearchIndex(Game[] snapshot)
        {
            var built = new Dictionary<int, string>(snapshot.Length);
            foreach (var g in snapshot)
            {
                if (g == null) continue;
                built[g.Id] = BuildSearchableText(g);
            }
            return built;
        }

        private Dictionary<int, string> GetOrBuildSearchIndex(Game[] snapshot)
        {
            var existing = _searchIndex;
            if (existing != null) return existing;

            var built = BuildSearchIndex(snapshot);
            _searchIndex = built;
            return built;
        }

        /// <summary>
        /// Builds the per-game searchable string: lowercased concatenation
        /// of every field we want a typed token to be able to hit. The '|'
        /// separator prevents accidental cross-field matches (e.g. "snesx"
        /// wouldn't match "snes" + "xevious" if they're adjacent fields).
        /// Year is included only when &gt; 0; "0" would otherwise be a
        /// universal match for the token "0".
        ///
        /// Description is intentionally excluded — multi-paragraph blurbs
        /// from ScreenScraper/OpenVGDB inflate noise (typing "the" would
        /// match almost everything).
        /// </summary>
        private static string BuildSearchableText(Game g)
        {
            var sb = new System.Text.StringBuilder();
            AppendTitle(sb, g.Title);
            AppendField(sb, g.Console);
            AppendField(sb, g.Developer);
            AppendField(sb, g.Publisher);
            AppendField(sb, g.Genre);
            if (g.Year > 0) AppendField(sb, g.Year.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// Title-specific appender: splits on '~' so regional alt-titles
        /// (e.g. "Pokemon Yellow ~ Special Pikachu Edition") index as
        /// independent chunks rather than carrying the literal '~' through.
        /// Token-based substring matching already worked through the tilde
        /// before — this is cosmetic cleanup + protects against the rare
        /// case where a user types '~' as a literal token (would otherwise
        /// false-positive every tilde'd title).
        /// </summary>
        private static void AppendTitle(System.Text.StringBuilder sb, string? title)
        {
            if (string.IsNullOrEmpty(title)) return;
            // No-tilde titles produce a single-element array; behavior
            // identical to a single AppendField call for the common case.
            foreach (var chunk in title.Split('~', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = chunk.Trim();
                if (trimmed.Length > 0) AppendField(sb, trimmed);
            }
        }

        private static void AppendField(System.Text.StringBuilder sb, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            string normalized = NormalizeForSearch(value);
            if (normalized.Length == 0) return;
            if (sb.Length > 0) sb.Append('|');
            sb.Append(normalized);
        }

        /// <summary>
        /// Lowercases + strips diacritics so search is accent-blind.
        /// `Normalize(FormD)` decomposes "é" into "e" + combining-acute;
        /// dropping all <see cref="System.Globalization.UnicodeCategory.NonSpacingMark"/>
        /// chars leaves just the base letters. Applied to BOTH index fields
        /// and query tokens so "pokemon" finds "Pokémon" and "ole" finds "Olé!".
        ///
        /// Internal so the Save States and Screenshots search paths
        /// (MainWindow code-behind) can normalize their inputs the same way
        /// without duplicating the implementation.
        /// </summary>
        internal static string NormalizeForSearch(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string decomposed;
            try
            {
                decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                // Ill-formed UTF-16 (e.g. a lone surrogate smuggled in via a
                // filename or DAT entry) makes Normalize throw. One bad title
                // must not take down the whole search pass — fall back to a
                // plain lowercase of the raw string (loses accent-blindness
                // for this one field only).
                return value.ToLowerInvariant();
            }
            var sb = new System.Text.StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c);
            }
            return sb.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// Pre-builds the per-console ObservableCollections in the background so
        /// clicking a console in the sidebar is instant (no sorting/allocation on UI thread).
        /// </summary>
        public Task PreloadConsoleCachesAsync()
        {
            return Task.Run(() =>
            {
                // Single pass: group + sort all games at once instead of N separate Where+OrderBy.
                var grouped = _allGames.GroupBy(g => g.Console)
                    .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Title).ToList());

                foreach (var (console, sorted) in grouped)
                {
                    if (!_consoleCache.ContainsKey(console))
                        _consoleCache[console] = new ObservableCollection<Game>(sorted);
                }

                if (!_consoleCache.ContainsKey("All Games"))
                {
                    var all = _allGames.OrderBy(g => g.Console).ThenBy(g => g.Title).ToList();
                    _consoleCache["All Games"] = new ObservableCollection<Game>(all);
                    _filterDirty = false;
                }

                // Pre-decode the first ~60 box-art images per console (roughly two
                // viewport pages at default card width) so the very first paint
                // after a console click is hot. Strong-ref tier pins them so the
                // GC doesn't reclaim before the user actually navigates.
                const int prefetchPerConsole = 60;
                foreach (var (_, sorted) in grouped)
                {
                    var paths = new List<string?>(prefetchPerConsole);
                    for (int i = 0; i < sorted.Count && i < prefetchPerConsole; i++)
                        paths.Add(sorted[i].DisplayArtPath);
                    _ = Emutastic.Converters.PathToImageConverter.PreloadAsync(paths);
                }
            });
        }

        private CancellationTokenSource? _statusClearCts;
        private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

        public void SetStatus(string msg, bool autoClear = false)
            => SetStatus(msg, autoClear ? 3000 : 0);

        /// <summary>
        /// Routes to the single bottom-left banner surface (NotificationText +
        /// IsNotification). All transient status updates go through here, so the
        /// app has exactly one place for status messages.
        /// </summary>
        public void SetStatus(string msg, int dwellMs)
        {
            _statusClearCts?.Cancel();
            // Imports + core-update flows still set their own IsImporting /
            // IsCoreUpdating properties, which BannerText prioritises over the
            // notification text. So a SetStatus call during an active import
            // won't stomp on the import progress — the notification just queues
            // behind it via the BannerText priority chain.
            NotificationText = msg;
            IsNotification   = true;
            // Keep StatusText in sync so any leftover bindings still see the
            // current message (no-op if nothing is bound to it).
            StatusText       = msg;
            if (dwellMs <= 0) return;
            _statusClearCts = new CancellationTokenSource();
            var token = _statusClearCts.Token;
            _ = Task.Delay(dwellMs, token).ContinueWith(_ =>
            {
                Action clear = () =>
                {
                    // Only clear if our text is still showing — don't stomp on a
                    // newer status that fired after us.
                    if (NotificationText == msg)
                    {
                        NotificationText = "";
                        IsNotification   = false;
                        StatusText       = "";
                    }
                };
                if (_uiContext != null) _uiContext.Post(_ => clear(), null);
                else                    clear();
            }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        public void InvalidateCache()
        {
            _filterDirty = true;
            _consoleCache.Clear();
            // Search index is built from a snapshot of _allGames; any mutation
            // that calls InvalidateCache (Add/Remove/Reload/RefreshGame) is also
            // a mutation we need to re-scan for next search.
            _searchIndex = null;
        }

        private void UpdateCount()
        {
            int count = Games.Count;
            GameCountText = count == 1 ? "1 game" : $"{count} games";
        }

        private static Game[] GetSampleGames() =>
        [
            new Game { Title = "Super Mario Bros.",     Console = "NES",     Manufacturer = "Nintendo", Year = 1985, BackgroundColor = "#C8102E", AccentColor = "#FF6B6B", PlayCount = 12, SaveCount = 3,  LastPlayed = DateTime.Now.AddDays(-2) },
            new Game { Title = "The Legend of Zelda",   Console = "NES",     Manufacturer = "Nintendo", Year = 1986, BackgroundColor = "#FFD700", AccentColor = "#FFA500", PlayCount = 8,  SaveCount = 5,  LastPlayed = DateTime.Now.AddDays(-7) },
            new Game { Title = "Super Mario World",     Console = "SNES",    Manufacturer = "Nintendo", Year = 1990, BackgroundColor = "#E63946", AccentColor = "#FF6B6B", PlayCount = 22, SaveCount = 8,  LastPlayed = DateTime.Now.AddDays(-1), IsFavorite = true },
            new Game { Title = "A Link to the Past",    Console = "SNES",    Manufacturer = "Nintendo", Year = 1991, BackgroundColor = "#2A9D8F", AccentColor = "#57CC99", PlayCount = 15, SaveCount = 12, LastPlayed = DateTime.Now.AddDays(-3), IsFavorite = true },
            new Game { Title = "Super Mario 64",        Console = "N64",     Manufacturer = "Nintendo", Year = 1996, BackgroundColor = "#E63946", AccentColor = "#FF6B6B", PlayCount = 18, SaveCount = 4,  LastPlayed = DateTime.Now.AddDays(-4) },
            new Game { Title = "Ocarina of Time",       Console = "N64",     Manufacturer = "Nintendo", Year = 1998, BackgroundColor = "#2A9D8F", AccentColor = "#57CC99", PlayCount = 11, SaveCount = 9,  LastPlayed = DateTime.Now.AddDays(-6), IsFavorite = true },
            new Game { Title = "Sonic the Hedgehog",    Console = "Genesis", Manufacturer = "Sega",     Year = 1991, BackgroundColor = "#0096FF", AccentColor = "#FFD700", PlayCount = 14, SaveCount = 0,  LastPlayed = DateTime.Now.AddDays(-3) },
            new Game { Title = "Symphony of the Night", Console = "PS1",     Manufacturer = "Sony",     Year = 1997, BackgroundColor = "#1A0A2E", AccentColor = "#9C27B0", PlayCount = 8,  SaveCount = 7,  LastPlayed = DateTime.Now.AddDays(-9), IsFavorite = true },
            new Game { Title = "Pokemon Red",           Console = "GB",      Manufacturer = "Nintendo", Year = 1996, BackgroundColor = "#CC0000", AccentColor = "#FF6B6B", PlayCount = 30, SaveCount = 1,  LastPlayed = DateTime.Now.AddDays(-1), IsFavorite = true },
            new Game { Title = "Tetris",                Console = "GB",      Manufacturer = "Nintendo", Year = 1989, BackgroundColor = "#1565C0", AccentColor = "#42A5F5", PlayCount = 45, SaveCount = 0,  LastPlayed = DateTime.Now.AddDays(-1) },
            new Game { Title = "Pokemon FireRed",       Console = "GBA",     Manufacturer = "Nintendo", Year = 2004, BackgroundColor = "#CC0000", AccentColor = "#FF6B6B", PlayCount = 20, SaveCount = 3,  LastPlayed = DateTime.Now.AddDays(-2) },
            new Game { Title = "Chrono Trigger",        Console = "SNES",    Manufacturer = "Nintendo", Year = 1995, BackgroundColor = "#264653", AccentColor = "#2A9D8F", PlayCount = 4,  SaveCount = 6,  LastPlayed = DateTime.Now.AddDays(-5), IsFavorite = true },
        ];
    }
}
