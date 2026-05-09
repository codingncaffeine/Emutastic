using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Emutastic.Models
{
    public class Game : INotifyPropertyChanged
    {
        // INPC is implemented narrowly: only the art-path properties notify.
        // This lets DisplayArtPath update live during import (when artwork
        // arrives async after a game is added to the list) without making
        // every Game property a notifying setter — most fields don't change
        // post-load, and full INPC would risk per-import-tile churn on
        // libraries that complete in seconds.
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Console { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public int Year { get; set; }
        public string RomPath { get; set; } = "";
        public string RomHash { get; set; } = "";

        private string _coverArtPath = "";
        public string CoverArtPath
        {
            get => _coverArtPath;
            set => SetArtPath(ref _coverArtPath, value);
        }

        private string _boxArt3DPath = "";
        public string BoxArt3DPath
        {
            get => _boxArt3DPath;
            set => SetArtPath(ref _boxArt3DPath, value);
        }

        private string _screenScraperArtPath = "";
        public string ScreenScraperArtPath
        {
            get => _screenScraperArtPath;
            set => SetArtPath(ref _screenScraperArtPath, value);
        }

        // Common setter: only notifies when the value actually changes, and
        // only fires DisplayArtPath when the *resolved* path changes — e.g.
        // a ScreenScraper path arriving while the user prefers libretro and
        // libretro art is already set produces zero notifications.
        private void SetArtPath(ref string field, string value, [CallerMemberName] string? name = null)
        {
            value ??= "";
            if (field == value) return;
            string prevDisplay = DisplayArtPath;
            field = value;
            OnPropertyChanged(name);
            if (DisplayArtPath != prevDisplay)
                OnPropertyChanged(nameof(DisplayArtPath));
        }

        /// <summary>
        /// Returns the best available art path based on user preferences:
        /// 3D > ScreenScraper 2D (when preferred) > libretro 2D > ScreenScraper 2D (fallback).
        /// </summary>
        public string DisplayArtPath
        {
            get
            {
                if (Consoles3D.Contains(Console) && !string.IsNullOrEmpty(BoxArt3DPath))
                    return BoxArt3DPath;
                if (PreferScreenScraper2D && !string.IsNullOrEmpty(ScreenScraperArtPath))
                    return ScreenScraperArtPath;
                if (!string.IsNullOrEmpty(CoverArtPath))
                    return CoverArtPath;
                // Last resort: show SS 2D art even if not preferred, better than nothing
                if (!string.IsNullOrEmpty(ScreenScraperArtPath))
                    return ScreenScraperArtPath;
                return "";
            }
        }

        /// <summary>Set of console tags that currently display 3D box art.</summary>
        public static HashSet<string> Consoles3D { get; set; } = new();

        /// <summary>When true, prefer ScreenScraper 2D art over libretro for display.</summary>
        public static bool PreferScreenScraper2D { get; set; }

        public string Developer { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Description { get; set; } = "";

        public string BackgroundColor { get; set; } = "#1F1F21";
        public string AccentColor { get; set; } = "#E03535";
        public int PlayCount { get; set; }
        public int SaveCount { get; set; }
        public bool IsFavorite { get; set; }
        private int _rating;
        public int Rating
        {
            get => _rating;
            set
            {
                if (_rating == value) return;
                _rating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RatingStars));
            }
        }
        public string Collection { get; set; } = "";
        public DateTime? LastPlayed { get; set; }
        public int ArtworkAttempts { get; set; }

        public string LastPlayedDisplay => LastPlayed.HasValue
            ? LastPlayed.Value.ToString("MMM d, yyyy")
            : "Never";

        public string PlayCountDisplay => PlayCount == 1
            ? "1 time"
            : $"{PlayCount} times";

        public string RatingStars => Rating switch
        {
            1 => "★☆☆☆☆",
            2 => "★★☆☆☆",
            3 => "★★★☆☆",
            4 => "★★★★☆",
            5 => "★★★★★",
            _ => "☆☆☆☆☆"
        };
    }
}