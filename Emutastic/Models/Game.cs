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

        // Preferred libretro core DLL filename (e.g. "mame2003_plus_libretro.dll").
        // Set at import time by ImportService for consoles with multiple cores
        // (currently only Arcade — FBNeo vs MAME 2003-Plus). Empty for games on
        // single-core consoles or legacy imports from before this column existed.
        // Launch path: CoreManager.GetCorePathForGame consults this first, falling
        // back to a fresh DAT lookup, then to the user's preferred-core setting.
        public string PreferredCore { get; set; } = "";

        public string Manufacturer { get; set; } = "";
        public int Year { get; set; }
        public string RomPath { get; set; } = "";
        // Path to the file the user actually selected at import time, before
        // any zip/7z extraction. For bare ROMs this matches RomPath. For
        // archive imports it points at the original archive in the user's
        // collection while RomPath points at the extracted file under
        // [DataRoot]\ExtractedRoms\. Used internally by Refresh Library so
        // a flat folder of zipped ROMs can be rescanned for new entries —
        // not surfaced in the UI.
        public string OriginalSourcePath { get; set; } = "";
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

        // Reference assignment is atomic on the CLR; we treat the field as
        // an immutable snapshot — the toggle helpers below swap in a fresh
        // HashSet instead of mutating the existing one. This makes the getter
        // safe to call from PathToImageConverter.PreloadAsync's background
        // thread while the UI user toggles 3D art for a console live.
        private static HashSet<string> _consoles3D = new();

        /// <summary>Set of console tags that currently display 3D box art.</summary>
        public static HashSet<string> Consoles3D
        {
            get => _consoles3D;
            set => _consoles3D = value ?? new();
        }

        public static void EnableConsole3D(string console)
        {
            var copy = new HashSet<string>(_consoles3D) { console };
            _consoles3D = copy;
        }

        public static void DisableConsole3D(string console)
        {
            var copy = new HashSet<string>(_consoles3D);
            copy.Remove(console);
            _consoles3D = copy;
        }

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

        /// <summary>
        /// Counts how many times the metadata pipeline (SS + OpenVGDB + ADB) has
        /// been run for this game. After at least one full attempt that didn't
        /// land complete metadata, the auto-resume filter excludes this game so
        /// it doesn't get retried on every launch. Manual Refresh Library on the
        /// game's console resets this counter so the user can force a re-try.
        /// </summary>
        public int MetadataAttempts { get; set; }

        // ── RetroAchievements cache ─────────────────────────────────────────
        // RAGameId is populated at game launch from rcheevos's identify-game
        // callback; 0 means "we haven't launched this game with RA enabled
        // yet" and the detail card hides the RA section accordingly.
        public int RAGameId { get; set; }

        // Raw last-fetched JSON from the RA web API. Lazy-deserialized into
        // typed views on first access via RAProgressionTyped / RAUserProgressTyped.
        // FetchedAt is unix seconds (0 = never fetched); used for TTL gating.
        public string RAProgressionJson { get; set; } = "";
        public long RAProgressionFetchedAt { get; set; }

        public string RAUserProgressJson { get; set; } = "";
        public long RAUserProgressFetchedAt { get; set; }

        // Lazy-deserialize the JSON on first access and invalidate when the
        // underlying string is reassigned. Callers don't pay the parse cost
        // until they actually read typed fields.
        private RAProgression? _raProgression;
        private string? _raProgressionJsonCached;
        public RAProgression? RAProgressionTyped
        {
            get
            {
                if (!ReferenceEquals(_raProgressionJsonCached, RAProgressionJson)
                    && _raProgressionJsonCached != RAProgressionJson)
                {
                    _raProgressionJsonCached = RAProgressionJson;
                    _raProgression = TryDeserialize<RAProgression>(RAProgressionJson);
                }
                return _raProgression;
            }
        }

        private RAUserProgress? _raUserProgress;
        private string? _raUserProgressJsonCached;
        public RAUserProgress? RAUserProgressTyped
        {
            get
            {
                if (!ReferenceEquals(_raUserProgressJsonCached, RAUserProgressJson)
                    && _raUserProgressJsonCached != RAUserProgressJson)
                {
                    _raUserProgressJsonCached = RAUserProgressJson;
                    _raUserProgress = TryDeserialize<RAUserProgress>(RAUserProgressJson);
                }
                return _raUserProgress;
            }
        }

        private static readonly System.Text.Json.JsonSerializerOptions _raJsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private static T? TryDeserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, _raJsonOpts); }
            catch { return null; }
        }

        /// <summary>True when the cached progression is older than <paramref name="ttl"/> or never fetched.</summary>
        public bool IsRAProgressionStale(TimeSpan ttl)
            => RAProgressionFetchedAt == 0
            || (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - RAProgressionFetchedAt) > (long)ttl.TotalSeconds;

        /// <summary>True when the cached per-user progress is older than <paramref name="ttl"/> or never fetched.</summary>
        public bool IsRAUserProgressStale(TimeSpan ttl)
            => RAUserProgressFetchedAt == 0
            || (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - RAUserProgressFetchedAt) > (long)ttl.TotalSeconds;

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