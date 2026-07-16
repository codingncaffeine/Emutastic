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

        // User's free-text notes for this game (Tier 2 notes feature). Notifying so
        // the detail-card preview updates live after editing. Synced for free via the
        // library.db GitHub backup — no separate sync wiring.
        private string _notes = "";
        public string Notes
        {
            get => _notes;
            set
            {
                value ??= "";
                if (_notes == value) return;
                _notes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasNotes));
            }
        }
        public bool HasNotes => !string.IsNullOrWhiteSpace(_notes);

        // Local path to the downloaded PDF manual (relative storage in DB, resolved
        // to absolute on read). Notifying so the "has manual" badge / menu label
        // (View vs Download) flips live after a download completes.
        private string _manualPath = "";
        public string ManualPath
        {
            get => _manualPath;
            set
            {
                value ??= "";
                if (_manualPath == value) return;
                _manualPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasManual));
            }
        }
        public bool HasManual => !string.IsNullOrEmpty(_manualPath);

        // Path to an IPS/BPS/UPS ROM-hack patch applied (in memory) to RomPath at launch.
        // Empty for normal games; set on hacked library entries. Relative storage in DB.
        private string _patchPath = "";
        public string PatchPath
        {
            get => _patchPath;
            set
            {
                value ??= "";
                if (_patchPath == value) return;
                _patchPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPatch));
            }
        }
        public bool HasPatch => !string.IsNullOrEmpty(_patchPath);

        // Path to an installed enhancement pack (Mesen HD pack folder, or a texture
        // pack folder for N64/GameCube/PSP). Empty for normal games; set on the
        // "(HD)" library entries created at pack install. The launch path pins the
        // capable core and force-enables its pack option when this is set.
        // Relative storage in DB (ToStoragePath/FromStoragePath).
        private string _hdPackPath = "";
        public string HdPackPath
        {
            get => _hdPackPath;
            set
            {
                value ??= "";
                if (_hdPackPath == value) return;
                _hdPackPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasHdPack));
            }
        }
        public bool HasHdPack => !string.IsNullOrEmpty(_hdPackPath);

        public string BackgroundColor { get; set; } = "#1F1F21";
        public string AccentColor { get; set; } = "#E03535";
        public int PlayCount { get; set; }
        public int SaveCount { get; set; }
        public int TotalPlayTimeSeconds { get; set; }
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

        // Live progress snapshot from the last play session (Phase 2).
        // Populated by EmulatorWindow at game-exit from rcheevos
        // PROGRESS_INDICATOR_UPDATE events. Read by the detail card to
        // upgrade "Coming up" from community-median proxies to real
        // "you're 73% of the way to X" progress.
        public string RALiveProgressJson { get; set; } = "";
        public long RALiveProgressFetchedAt { get; set; }

        // Outcome of the last rcheevos identification attempt. Lets the
        // Detail card distinguish "never launched with RA on" from
        // "launched, rcheevos said no achievement set" from "ROM hash
        // unrecognized." Values: "" (never attempted), "identified",
        // "not_in_database", or "load_failed".
        public string RALastLaunchOutcome { get; set; } = "";

        // Lazy-deserialize the JSON on first access and invalidate when the
        // underlying string is reassigned. The fetch service mutates the JSON
        // properties from a background thread while the UI thread reads the
        // typed views, so the cache check + assignment is locked. The lock
        // is uncontended in the common case (single reader, infrequent writer)
        // so the overhead is just a couple of monitor instructions.
        private readonly object _raTypedLock = new();
        private RAProgression? _raProgression;
        private string? _raProgressionJsonCached;
        public RAProgression? RAProgressionTyped
        {
            get
            {
                // Snapshot the live JSON once — the property field could change
                // mid-method on a writer thread otherwise.
                string current = RAProgressionJson;
                lock (_raTypedLock)
                {
                    if (!string.Equals(_raProgressionJsonCached, current, StringComparison.Ordinal))
                    {
                        _raProgressionJsonCached = current;
                        _raProgression = TryDeserialize<RAProgression>(current);
                    }
                    return _raProgression;
                }
            }
        }

        private RAUserProgress? _raUserProgress;
        private string? _raUserProgressJsonCached;
        public RAUserProgress? RAUserProgressTyped
        {
            get
            {
                string current = RAUserProgressJson;
                lock (_raTypedLock)
                {
                    if (!string.Equals(_raUserProgressJsonCached, current, StringComparison.Ordinal))
                    {
                        _raUserProgressJsonCached = current;
                        _raUserProgress = TryDeserialize<RAUserProgress>(current);
                    }
                    return _raUserProgress;
                }
            }
        }

        private RALiveProgress? _raLiveProgress;
        private string? _raLiveProgressJsonCached;
        public RALiveProgress? RALiveProgressTyped
        {
            get
            {
                string current = RALiveProgressJson;
                lock (_raTypedLock)
                {
                    if (!string.Equals(_raLiveProgressJsonCached, current, StringComparison.Ordinal))
                    {
                        _raLiveProgressJsonCached = current;
                        _raLiveProgress = TryDeserialize<RALiveProgress>(current);
                    }
                    return _raLiveProgress;
                }
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