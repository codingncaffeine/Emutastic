using Emutastic.Models;
using Emutastic.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

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
            set { _games = value; OnPropertyChanged(); }
        }

        private string _selectedConsole = "All Games";
        public string SelectedConsole
        {
            get => _selectedConsole;
            set { _selectedConsole = value; OnPropertyChanged(); FilterGames(); }
        }

        private string _gameCountText = "";
        public string GameCountText
        {
            get => _gameCountText;
            set { _gameCountText = value; OnPropertyChanged(); }
        }

        public MainViewModel(DatabaseService db)
        {
            _db = db;
            Reload();
        }

        public void Reload()
        {
            _allGames = new ObservableCollection<Game>(_db.GetAllGames());
            if (_allGames.Count == 0)
                _allGames = new ObservableCollection<Game>(GetSampleGames());
            FilterGames();
        }

        public void AddGame(Game game)
        {
            _allGames.Add(game);
            FilterGames();
        }

        public void RefreshGame(Game updated)
        {
            var existing = _allGames.FirstOrDefault(g => g.Id == updated.Id);
            if (existing != null)
            {
                int idx = _allGames.IndexOf(existing);
                _allGames[idx] = updated;
            }

            var inView = Games.FirstOrDefault(g => g.Id == updated.Id);
            if (inView != null)
            {
                int idx = Games.IndexOf(inView);
                Games[idx] = updated;
            }
        }

        public void RemoveGame(Game game)
        {
            var inAll = _allGames.FirstOrDefault(g => g.Id == game.Id);
            var inView = Games.FirstOrDefault(g => g.Id == game.Id);
            if (inAll != null) _allGames.Remove(inAll);
            if (inView != null) Games.Remove(inView);
            UpdateCount();
        }

        public void FilterGames()
        {
            var filtered = SelectedConsole == "All Games"
                ? _allGames.ToList()
                : _allGames.Where(g => g.Console == SelectedConsole).ToList();

            Games = new ObservableCollection<Game>(filtered);
            UpdateCount();
        }

        public void LoadFavorites(DatabaseService db)
        {
            var favs = db.GetFavorites();
            Games = new ObservableCollection<Game>(favs);
            UpdateCount();
        }

        public void LoadRecent(DatabaseService db)
        {
            var recent = db.GetRecentlyPlayed();
            Games = new ObservableCollection<Game>(recent);
            UpdateCount();
        }

        public void SearchGames(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { FilterGames(); return; }
            var filtered = _allGames
                .Where(g => g.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || g.Console.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Games = new ObservableCollection<Game>(filtered);
            GameCountText = filtered.Count == 1 ? "1 result" : $"{filtered.Count} results";
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