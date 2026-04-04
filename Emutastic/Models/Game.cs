using System;

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
        public string BackgroundColor { get; set; } = "#1F1F21";
        public string AccentColor { get; set; } = "#E03535";
        public int PlayCount { get; set; }
        public int SaveCount { get; set; }
        public bool IsFavorite { get; set; }
        public int Rating { get; set; }
        public string Collection { get; set; } = "";
        public DateTime? LastPlayed { get; set; }

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