using Emutastic.Models;
using Emutastic.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Emutastic.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly DatabaseService _db;
        private ObservableCollection<Game> _allGames = new();

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

        private string _selectedConsole = "All Games";
        public string SelectedConsole
        {
            get => _selectedConsole;
            set { _selectedConsole = value; OnPropertyChanged(); }
        }

        // Cached "All Games" flat-list result — reused across console-switch round trips.
        // Invalidated whenever games are added, removed, or reloaded.
        private ObservableCollection<Game>? _cachedAllGames;
        private bool _filterDirty = true;

        private string _gameCountText = "";
        public string GameCountText
        {
            get => _gameCountText;
            set { _gameCountText = value; OnPropertyChanged(); }
        }

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

        private bool _isGroupedView;
        public bool IsGroupedView
        {
            get => _isGroupedView;
            set { _isGroupedView = value; OnPropertyChanged(); }
        }

        public MainViewModel(DatabaseService db)
        {
            _db = db;
            // Data is loaded asynchronously by the caller via Reload() + FilterGamesAsync().
        }

        public void Reload()
        {
            var games = _db.GetAllGames();
            _allGames = games.Count == 0
                ? new ObservableCollection<Game>(GetSampleGames())
                : new ObservableCollection<Game>(games);
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
                if (!string.IsNullOrEmpty(updated.CoverArtPath)) target.CoverArtPath = updated.CoverArtPath;
                if (!string.IsNullOrEmpty(updated.BoxArt3DPath)) target.BoxArt3DPath = updated.BoxArt3DPath;
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

            var existing = _allGames.FirstOrDefault(g => g.Id == updated.Id);
            if (existing != null)
            {
                MergeOnto(existing);
                int idx = _allGames.IndexOf(existing);
                _allGames[idx] = existing; // re-seat to trigger collection change
            }
            else
            {
                _allGames.Add(updated);
            }

            var inView = Games.FirstOrDefault(g => g.Id == updated.Id);
            if (inView != null)
            {
                if (inView != existing) MergeOnto(inView);
                int idx = Games.IndexOf(inView);
                Games[idx] = inView; // re-seat to trigger collection change
            }
        }

        public void RefreshAllGames()
        {
            // Reassign to a new collection so the property-change fires and all bindings refresh.
            Games = new ObservableCollection<Game>(Games);
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
            var console = _selectedConsole;

            // Cache hit for "All Games" flat list.
            if (console == "All Games" && !_filterDirty && _cachedAllGames != null)
            {
                Games = _cachedAllGames;
                UpdateCount();
                return;
            }

            List<Game> result = null!;
            await Task.Run(() =>
            {
                result = console == "All Games"
                    ? _allGames.OrderBy(g => g.Console).ThenBy(g => g.Title).ToList()
                    : _allGames.Where(g => g.Console == console).OrderBy(g => g.Title).ToList();
            });

            var oc = new ObservableCollection<Game>(result);
            if (console == "All Games")
            {
                _cachedAllGames = oc;
                _filterDirty    = false;
            }

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

        public void SearchGames(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { _ = FilterGamesAsync(); return; }
            var filtered = _allGames
                .Where(g => g.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || g.Console.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Games = new ObservableCollection<Game>(filtered);
            IsGroupedView = false;
            GameCountText = filtered.Count == 1 ? "1 result" : $"{filtered.Count} results";
        }

        private void InvalidateCache()
        {
            _filterDirty    = true;
            _cachedAllGames = null;
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
            new Game { Title = "Pokémon Red",           Console = "GB",      Manufacturer = "Nintendo", Year = 1996, BackgroundColor = "#CC0000", AccentColor = "#FF6B6B", PlayCount = 30, SaveCount = 1,  LastPlayed = DateTime.Now.AddDays(-1), IsFavorite = true },
            new Game { Title = "Tetris",                Console = "GB",      Manufacturer = "Nintendo", Year = 1989, BackgroundColor = "#1565C0", AccentColor = "#42A5F5", PlayCount = 45, SaveCount = 0,  LastPlayed = DateTime.Now.AddDays(-1) },
            new Game { Title = "Pokémon FireRed",       Console = "GBA",     Manufacturer = "Nintendo", Year = 2004, BackgroundColor = "#CC0000", AccentColor = "#FF6B6B", PlayCount = 20, SaveCount = 3,  LastPlayed = DateTime.Now.AddDays(-2) },
            new Game { Title = "Chrono Trigger",        Console = "SNES",    Manufacturer = "Nintendo", Year = 1995, BackgroundColor = "#264653", AccentColor = "#2A9D8F", PlayCount = 4,  SaveCount = 6,  LastPlayed = DateTime.Now.AddDays(-5), IsFavorite = true },
        ];

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}