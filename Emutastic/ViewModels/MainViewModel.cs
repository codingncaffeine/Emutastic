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

        public bool IsBannerVisible => IsImporting || IsCoreUpdating || IsNotification;
        public bool IsProgressBarVisible => IsImporting || IsCoreUpdating;

        // Priority: import > core-update > notification (most-active-task wins).
        public string BannerText =>
            IsImporting     ? ImportStatusText :
            IsCoreUpdating  ? CoreUpdateText :
                              NotificationText;

        public double BannerProgressPercent =>
            IsImporting     ? ImportProgressPercent :
            IsCoreUpdating  ? CoreUpdateProgressPercent :
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
        public IRelayCommand NavigateToRecentCommand { get; }
        public IRelayCommand NavigateToFavoritesCommand { get; }
        public IRelayCommand NavigateToRecentlyAddedCommand { get; }
        public IRelayCommand<int> NavigateToCollectionCommand { get; }

        public MainViewModel(DatabaseService db)
        {
            _db = db;

            NavigateToConsoleCommand = new AsyncRelayCommand<string>(NavigateToConsoleAsync);
            NavigateToAllGamesCommand = new AsyncRelayCommand(NavigateToAllGamesAsync);
            NavigateToRecentCommand = new RelayCommand(NavigateToRecent);
            NavigateToFavoritesCommand = new RelayCommand(NavigateToFavorites);
            NavigateToRecentlyAddedCommand = new RelayCommand(NavigateToRecentlyAdded);
            NavigateToCollectionCommand = new RelayCommand<int>(NavigateToCollection);
        }

        private async Task NavigateToConsoleAsync(string? tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            IsShowingFavorites = false;
            IsMixedView = false;
            SelectedConsole = tag;
            await FilterGamesAsync();
            Navigated?.Invoke(tag);
        }

        private async Task NavigateToAllGamesAsync()
        {
            IsShowingFavorites = false;
            IsMixedView = true;
            SelectedConsole = "All Games";
            await FilterGamesAsync();
            ToolbarTitle = "All Games";
            Navigated?.Invoke("All Games");
        }

        private void NavigateToRecent()
        {
            IsShowingFavorites = false;
            IsMixedView = true;
            LoadRecent(_db);
            ToolbarTitle = "Recently Played";
            Navigated?.Invoke("Recent");
        }

        private void NavigateToFavorites()
        {
            IsShowingFavorites = true;
            IsMixedView = true;
            LoadFavorites(_db);
            ToolbarTitle = "Favorites";
            Navigated?.Invoke("Favorites");
        }

        private void NavigateToRecentlyAdded()
        {
            IsShowingFavorites = false;
            IsMixedView = true;
            var games = _db.GetRecentlyAdded(25);
            Games = new ObservableCollection<Game>(games);
            IsGroupedView = false;
            GameCountText = $"{games.Count} games";
            ToolbarTitle = "Recently Added";
            Navigated?.Invoke("RecentlyAdded");
        }

        private void NavigateToCollection(int collectionId)
        {
            IsShowingFavorites = false;
            IsMixedView = true;
            var games = _db.GetGamesByCollectionId(collectionId);
            Games = new ObservableCollection<Game>(games);
            IsGroupedView = false;
            GameCountText = $"{games.Count} games";
            Navigated?.Invoke($"Collection:{collectionId}");
        }

        public void Reload()
        {
            var games = _db.GetAllGames();
            _allGames = new ObservableCollection<Game>(games);
            _gameIndex = games.ToDictionary(g => g.Id);
            InvalidateCache();
        }

        public void AddGame(Game game)
        {
            _allGames.Add(game);
            InvalidateCache();
            // Filter update is handled by the caller (RefreshGame covers UI updates during import).
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
                if (updated.IsFavorite)      target.IsFavorite = true;
                if (updated.Rating > 0)      target.Rating = updated.Rating;
                if (updated.LastPlayed != null) target.LastPlayed = updated.LastPlayed;
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
            var favs = db.GetFavorites();
            Games = new ObservableCollection<Game>(favs);
            IsGroupedView = false;
            InvalidateCache();
            UpdateCount();
        }

        public void LoadRecent(DatabaseService db)
        {
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

        public async void SearchGames(string query)
        {
            // Cancel anything in flight from the previous keystroke.
            _searchCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _searchCts = cts;
            var token = cts.Token;

            if (string.IsNullOrWhiteSpace(query))
            {
                await FilterGamesAsync();
                return;
            }

            GameCountText = "Searching…";

            // Debounce: wait briefly before actually searching. If the user keeps
            // typing within 180ms, this Task.Delay throws and we exit cleanly.
            try { await Task.Delay(180, token); }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested) return;

            // Tokenize on whitespace — "zelda ocarina" should match
            // "The Legend of Zelda: Ocarina of Time" even though the words
            // aren't adjacent in the title.
            var tokens = query
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant())
                .ToArray();
            if (tokens.Length == 0)
            {
                await FilterGamesAsync();
                return;
            }

            List<Game>? filtered = null;
            try
            {
                await Task.Run(() =>
                {
                    filtered = _allGames
                        .Where(g => g != null && MatchesAllTokens(g, tokens))
                        .ToList();
                }, token);
            }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested || filtered == null) return;

            Games = new ObservableCollection<Game>(filtered);
            IsGroupedView = false;
            GameCountText = filtered.Count == 1 ? "1 result" : $"{filtered.Count} results";
        }

        private static bool MatchesAllTokens(Game g, string[] lowerTokens)
        {
            // Null-safe: a missing title shouldn't blow up the whole query.
            string title   = (g.Title   ?? "").ToLowerInvariant();
            string console = (g.Console ?? "").ToLowerInvariant();
            foreach (var t in lowerTokens)
            {
                if (!title.Contains(t) && !console.Contains(t))
                    return false;
            }
            return true;
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
        /// Overload with explicit dwell duration (ms). Pass 0 (or use the
        /// <see cref="SetStatus(string, bool)"/> overload with autoClear=false)
        /// for a sticky message.
        /// </summary>
        public void SetStatus(string msg, int dwellMs)
        {
            _statusClearCts?.Cancel();
            StatusText = msg;
            if (dwellMs <= 0) return;
            _statusClearCts = new CancellationTokenSource();
            var token = _statusClearCts.Token;
            _ = Task.Delay(dwellMs, token).ContinueWith(_ =>
            {
                if (_uiContext != null)
                    _uiContext.Post(_ => StatusText = "", null);
                else
                    StatusText = "";
            }, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        public void InvalidateCache()
        {
            _filterDirty = true;
            _consoleCache.Clear();
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
