using System;
using System.Collections.Generic;

namespace Emutastic.Models
{
    public class Game
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Console { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public int Year { get; set; }
        public string RomPath { get; set; } = "";
        public string RomHash { get; set; } = "";
        public string CoverArtPath { get; set; } = "";
        public string BoxArt3DPath { get; set; } = "";
        public string ScreenScraperArtPath { get; set; } = "";

        /// <summary>
        /// Returns the best available art path based on user preferences:
        /// 3D box art (when enabled for this console) > ScreenScraper 2D (when preferred) > libretro 2D.
        /// </summary>
        public string DisplayArtPath
        {
            get
            {
                if (Consoles3D.Contains(Console) && !string.IsNullOrEmpty(BoxArt3DPath))
                    return BoxArt3DPath;
                if (PreferScreenScraper2D && !string.IsNullOrEmpty(ScreenScraperArtPath))
                    return ScreenScraperArtPath;
                return CoverArtPath;
            }
        }

        /// <summary>Set of console tags that currently display 3D box art.</summary>
        public static HashSet<string> Consoles3D { get; set; } = new();

        /// <summary>When true, prefer ScreenScraper 2D art over libretro for display.</summary>
        public static bool PreferScreenScraper2D { get; set; }

        public string BackgroundColor { get; set; } = "#1F1F21";
        public string AccentColor { get; set; } = "#E03535";
        public int PlayCount { get; set; }
        public int SaveCount { get; set; }
        public bool IsFavorite { get; set; }
        public int Rating { get; set; }
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