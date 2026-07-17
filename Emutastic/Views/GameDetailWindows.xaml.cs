using Emutastic.Configuration;
using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Views;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Emutastic.Views
{
    public partial class GameDetailWindow : Window
    {
        private Game _game;
        private readonly DatabaseService _db = new();

        // Cancellation source for the in-flight RA refresh; cancelled on
        // window close so a slow API response doesn't write back into a
        // Game object after a fresh detail card has already taken over.
        private System.Threading.CancellationTokenSource? _raRefreshCts;

        // LibVLC instance lives in VideoPlaybackService — warmed off the UI thread at
        // app startup so the first detail-window open doesn't pay the multi-second
        // native init cost on the dispatcher.
        private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;
        private WriteableBitmap? _videoBitmap;
        private IntPtr _videoBuffer;
        private int _videoWidth, _videoHeight;
        private bool _crossfadeDone;

        // Signals OnClosed → in-flight PlaySnapVideoAsync worker that the window
        // is gone, so the worker drops its half-constructed MediaPlayer instead
        // of stashing it on a dead window. Volatile so the worker thread sees
        // the UI-thread write without a memory barrier dance.
        private volatile bool _closed;

        public GameDetailWindow(Game game)
        {
            InitializeComponent();
            _game = game;
            PopulateData();
            AnimateIn();
            _ = LoadSnapAsync();
            _ = LoadRetroAchievementsAsync();
        }

        private void PopulateData()
        {
            GameTitle.Text = _game.Title;
            ConsoleTag.Text = _game.Console;
            ArtPlaceholderText.Text = _game.Title;

            // Metadata pills
            bool hasYear = _game.Year > 0;
            bool hasDev = !string.IsNullOrEmpty(_game.Developer);
            bool hasGenre = !string.IsNullOrEmpty(_game.Genre);
            bool hasDesc = !string.IsNullOrEmpty(_game.Description);

            if (hasYear || hasDev || hasGenre)
            {
                MetadataPanel.Visibility = Visibility.Visible;

                if (hasYear)
                {
                    YearPill.Visibility = Visibility.Visible;
                    GameYear.Text = _game.Year.ToString();
                }

                if (hasDev)
                {
                    DeveloperPill.Visibility = Visibility.Visible;
                    GameDeveloper.Text = !string.IsNullOrEmpty(_game.Publisher)
                        && _game.Publisher != _game.Developer
                        ? $"{_game.Developer}  ·  {_game.Publisher}"
                        : _game.Developer;
                }

                if (hasGenre)
                {
                    GenrePill.Visibility = Visibility.Visible;
                    // Show first genre only (e.g. "Action" from "Action,Platformer,2D")
                    string genre = _game.Genre;
                    int comma = genre.IndexOf(',');
                    GameGenre.Text = comma > 0 ? genre.Substring(0, comma) : genre;
                }
            }

            if (hasDesc)
            {
                GameDescriptionScroll.Visibility = Visibility.Visible;
                GameDescription.Text = _game.Description;
            }

            UpdateStatPills();
            FavoriteBadge.Visibility = _game.IsFavorite
                ? Visibility.Visible
                : Visibility.Collapsed;
            FavoriteButton.Content = _game.IsFavorite ? "♥  Favorited" : "♡  Favorite";

            // Set art background color
            if (System.Windows.Media.ColorConverter.ConvertFromString(_game.BackgroundColor)
                is System.Windows.Media.Color color)
            {
                ArtBgBrush.Color = color;
            }
        }

        private void RefreshStats()
        {
            UpdateStatPills();
        }

        /// <summary>
        /// Renders the inline stats pills (Times Played / Play Time / Last
        /// Played) alongside the meta pills. Each pill hides when its value
        /// is zero/Never, so the right cluster doesn't get noisy for a freshly
        /// imported game.
        /// </summary>
        private void UpdateStatPills()
        {
            int plays = _game.PlayCount;
            int totalSec = _game.TotalPlayTimeSeconds;
            bool everPlayed = _game.LastPlayed.HasValue;

            if (plays > 0)
            {
                StatPlayed.Text = plays == 1 ? "1 play" : $"{plays} plays";
                PlayedPill.Visibility = Visibility.Visible;
            }
            else
            {
                PlayedPill.Visibility = Visibility.Collapsed;
            }

            if (totalSec > 0)
            {
                StatPlayTime.Text = FormatDuration(totalSec);
                PlayTimePill.Visibility = Visibility.Visible;
            }
            else
            {
                PlayTimePill.Visibility = Visibility.Collapsed;
            }

            if (everPlayed)
            {
                StatLastPlayed.Text = _game.LastPlayedDisplay;
                LastPlayedPill.Visibility = Visibility.Visible;
            }
            else
            {
                LastPlayedPill.Visibility = Visibility.Collapsed;
            }
        }

        // ── Snap loading: video (ScreenScraper) → image (libretro) → placeholder ──

        private async System.Threading.Tasks.Task LoadSnapAsync()
        {
            try
            {
                // Show cover art immediately as a placeholder while video loads.
                // Decode happens on a worker; UI thread only touches HeaderImage.
                await ShowCoverArtPlaceholderAsync();

                // 1 — try ScreenScraper video snap if configured
                var snapConfig = App.Configuration?.GetSnapConfiguration();
                if (snapConfig is { ScreenScraperEnabled: true }
                    && !string.IsNullOrWhiteSpace(snapConfig.ScreenScraperUser))
                {
                    var ss = new ScreenScraperService();

                    // Check cache first (instant, no network)
                    string? cached = ss.FindCachedSnap(_game.RomHash, _game.Console);
                    if (cached == null)
                        cached = await ss.FetchSnapAsync(
                            snapConfig.ScreenScraperUser, snapConfig.ScreenScraperPassword,
                            _game.Console, _game.RomHash, _game.RomPath);

                    if (cached != null)
                    {
                        await PlaySnapVideoAsync(cached);
                        return;
                    }
                }

                // 2 — fall back to static libretro screenshot
                var artworkService = new ArtworkService();
                string? snapPath = await artworkService.FetchSnapAsync(
                    _game.RomHash, _game.RomPath, _game.Console);

                if (snapPath == null || !System.IO.File.Exists(snapPath)) return;

                var bitmap = await System.Threading.Tasks.Task.Run(() =>
                {
                    var bm = new BitmapImage();
                    bm.BeginInit();
                    bm.UriSource = new Uri(snapPath, UriKind.Absolute);
                    bm.CacheOption = BitmapCacheOption.OnLoad;
                    bm.EndInit();
                    bm.Freeze();
                    return bm;
                });

                if (_closed) return;
                HeaderImage.Source = bitmap;
                HeaderImage.Visibility = Visibility.Visible;
                ArtPlaceholderText.Visibility = Visibility.Collapsed;
            }
            catch { /* cosmetic — silently ignore */ }
        }

        // ── RetroAchievements section ─────────────────────────────────────

        /// <summary>
        /// Renders the RA section using whatever's cached, then fires a
        /// background refresh; on completion the section re-renders with
        /// fresh data. Bails fast for games that have never been launched
        /// with RA enabled (RAGameId == 0) and for users who haven't entered
        /// a Web API key.
        /// </summary>
        private async System.Threading.Tasks.Task LoadRetroAchievementsAsync()
        {
            // Hide the section entirely for users who haven't opted into RA.
            // We don't want to pester them with empty achievement UI.
            var raConfig = App.Configuration?.GetRetroAchievementsConfiguration();
            if (raConfig == null || !raConfig.IsConfigured)
            {
                RASection.Visibility = Visibility.Collapsed;
                return;
            }

            // Render whatever's already cached (or show a status placeholder
            // when there's nothing to render — the user wants to know whether
            // they've engaged with RA on this title, regardless of progress).
            RenderRetroAchievements();

            // Refresh if either cache is stale (no-op when fresh / no API key).
            _raRefreshCts?.Cancel();
            _raRefreshCts = new System.Threading.CancellationTokenSource();
            var token = _raRefreshCts.Token;
            try
            {
                if (App.Configuration == null) return;
                var svc = new RetroAchievementsService(App.Configuration, _db);
                await svc.RefreshDetailForGameAsync(_game, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || !IsLoaded) return;
                RenderRetroAchievements();
            }
            catch (OperationCanceledException) { /* window closed during fetch */ }
            catch
            {
                // Network-side failures are already swallowed inside the
                // service; this catch is the belt for any DB / render glitch
                // so a flaky API can never crash the card.
            }
        }

        /// <summary>
        /// Reads current cached typed views from the Game and pushes data into
        /// the UI. Safe to call multiple times — re-renders from scratch each
        /// invocation. Handles missing data gracefully (hides sections piece
        /// by piece rather than the whole pane).
        /// </summary>
        private void RenderRetroAchievements()
        {
            var prog = _game.RAProgressionTyped;
            var user = _game.RAUserProgressTyped;

            // No progression data yet — show a single labeled status line
            // instead of an empty section so the user can tell at a glance
            // why nothing is unlocking. Distinguishes "never launched with
            // RA" from "rcheevos says no set exists" from "ROM unrecognized"
            // — three states that look identical otherwise. The label is
            // driven by RALastLaunchOutcome which the launch path persists
            // alongside RAGameId.
            if (prog == null || prog.NumAchievements <= 0)
            {
                // Distinguish four states for an identified game (RAGameId > 0):
                //   - RAGameId >= 1_000_000_000: RA's "unsupported version"
                //     placeholder ID (1B+base_game_id). The server returns 404
                //     on every endpoint for these because no set is authored
                //     against this specific ROM hash — even though the game
                //     family is known. Don't say "Fetching…" — nothing will
                //     ever come.
                //   - prog non-null with NumAchievements == 0: server returned
                //     a real response and the game has zero authored
                //     achievements (e.g. Dungeon Explorer II, Bonanza Bros.).
                //   - prog null: fetch hasn't completed yet or stored response
                //     was empty; legitimately "Fetching…".
                //   - RAGameId == 0: never identified, distinct from the above.
                bool identified  = _game.RAGameId > 0;
                bool unsupported = _game.RAGameId >= 1_000_000_000;
                bool emptySet    = prog != null && prog.NumAchievements <= 0;
                string status = (identified, unsupported, emptySet, _game.RALastLaunchOutcome) switch
                {
                    (true,  true,  _,    _)                  => "This ROM dump isn't on the RetroAchievements database — try a different release",
                    (true,  false, true, _)                  => "No achievements authored for this game yet",
                    (true,  false, false, _)                 => "Fetching achievement data…",
                    (false, _,     _,    "not_in_database") => UnrecognizedHashMessage(_game.Console),
                    (false, _,     _,    "load_failed")     => "RetroAchievements identification failed — try relaunching",
                    _                                         => "Not checked yet — launch this game with RetroAchievements enabled",
                };
                RASection.Visibility = Visibility.Visible;
                RAProgressLabel.Text = status;
                RAProgress.Value = 0;
                RAProgress.Visibility = Visibility.Collapsed;
                ComingUpSection.Visibility = Visibility.Collapsed;
                RATimingsCaption.Visibility = Visibility.Collapsed;
                return;
            }

            RASection.Visibility = Visibility.Visible;
            RAProgress.Visibility = Visibility.Visible;

            // Pick the unlock track that matches the user's hardcore setting.
            // In hardcore mode the user cares about hardcore unlocks (gated on
            // no save-states / no rewind); in softcore mode the regular column
            // tracks every unlock. Falls back to softcore when not logged in
            // or when no config is available.
            bool hardcore = App.Configuration?.GetRetroAchievementsConfiguration()?.HardcoreMode == true;

            // Header: "12 / 47 · 1,240 pts" — pts is what the user has, not the
            // game total, when logged in. Logged-out: just "47 achievements".
            int total  = prog.NumAchievements;
            int earned = hardcore
                ? (user?.NumAwardedToUserHardcore ?? 0)
                : (user?.NumAwardedToUser ?? 0);
            int userPts = 0;
            if (user != null)
            {
                foreach (var a in user.Achievements.Values)
                {
                    string? earnedDate = hardcore ? a.DateEarnedHardcore : a.DateEarned;
                    if (!string.IsNullOrEmpty(earnedDate)) userPts += a.Points;
                }
            }

            if (user != null)
            {
                RAProgressLabel.Text = userPts > 0
                    ? $"{earned} / {total}  ·  {userPts:N0} pts"
                    : $"{earned} / {total}";
                RAProgress.Value = total > 0 ? earned * 100.0 / total : 0;
                // Mastered (100%): switch the bar to gold; otherwise accent.
                RAProgress.Foreground = (earned >= total && total > 0)
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D))
                    : (Brush)FindResource("AccentBrush");
            }
            else
            {
                RAProgressLabel.Text = $"{total} achievements";
                RAProgress.Value = 0;
            }

            // "Coming up" — top 3 unearned by ascending median time-to-unlock.
            BuildComingUp(prog, user, hardcore);

            // Typical-run caption — only when sample sizes are high enough
            // to trust the median (n >= 20 is a community convention).
            BuildTimingsCaption(prog, hardcore);
        }

        private void BuildComingUp(RAProgression prog, RAUserProgress? user, bool hardcore)
        {
            ComingUpGrid.Children.Clear();

            // Logged-out users have no "earned" set, so every achievement
            // would be "Coming up" — meaningless. Hide the section instead.
            if (user == null || user.Achievements.Count == 0)
            {
                ComingUpSection.Visibility = Visibility.Collapsed;
                return;
            }

            // Build a HashSet of earned achievement IDs for O(1) lookup.
            // "Earned" is hardcore-specific when the user runs hardcore.
            var earnedIds = new HashSet<int>();
            foreach (var a in user.Achievements.Values)
            {
                string? earnedDate = hardcore ? a.DateEarnedHardcore : a.DateEarned;
                if (!string.IsNullOrEmpty(earnedDate)) earnedIds.Add(a.Id);
            }

            // Phase-2 picker: prefer achievements with live in-game progress
            // (collected by rcheevos in the user's last session). Sort live-
            // progress hits descending by percent — "closest to unlocking
            // right now" — and fill remaining slots with the Phase-1 web-API
            // proxy (ascending median TTU, tiebreak descending popularity).
            //
            // Skip any community-median candidate with a null/zero median —
            // that's a no-data signal, not "instant." Hardcore mode picks
            // the hardcore-flavoured median + unlock count when available.
            //
            // Live progress is only trusted when its captured mode matches the
            // user's current mode — softcore-captured "73%" is meaningless
            // under hardcore (different ruleset, server resets state on mode
            // switch). Mode-mismatched data falls through to the proxy.
            var live = _game.RALiveProgressTyped;
            Dictionary<int, RALiveAchievementProgress> liveMap;
            if (live != null && live.Hardcore == hardcore)
                liveMap = live.Achievements;
            else
                liveMap = new Dictionary<int, RALiveAchievementProgress>();

            var unearned = prog.Achievements
                .Where(a => !earnedIds.Contains(a.Id))
                .ToList();

            // Bucket A: live progress > 0 (and not at 100% — that's a quirk
            // where rcheevos fires the event right before the unlock).
            var liveHits = unearned
                .Where(a => liveMap.TryGetValue(a.Id, out var lp) && lp.Percent > 0 && lp.Percent < 100)
                .OrderByDescending(a => liveMap[a.Id].Percent)
                .ToList();

            // ID-based dedup — RAAchievement has no Equals override, so
            // reference-equality only works by accident from the shared
            // `unearned` list. A HashSet<int> of live-hit IDs survives any
            // future refactor that projects/copies achievements.
            var liveHitIds = new HashSet<int>(liveHits.Select(a => a.Id));

            // Bucket B: web-API proxy fallback.
            var proxyPool = unearned
                .Where(a => !liveHitIds.Contains(a.Id))
                .Select(a => new
                {
                    Ach = a,
                    Median = hardcore ? (a.MedianTimeToUnlockHardcore ?? a.MedianTimeToUnlock) : a.MedianTimeToUnlock,
                    Pop    = hardcore ? a.NumAwardedHardcore : a.NumAwarded,
                })
                .Where(x => x.Median.HasValue && x.Median.Value > 0)
                .OrderBy(x => x.Median!.Value)
                .ThenByDescending(x => x.Pop)
                .Select(x => x.Ach)
                .ToList();

            var picks = liveHits.Concat(proxyPool).Take(3).ToList();

            if (picks.Count == 0)
            {
                ComingUpSection.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var ach in picks)
            {
                int median = (hardcore ? (ach.MedianTimeToUnlockHardcore ?? ach.MedianTimeToUnlock) : ach.MedianTimeToUnlock) ?? 0;
                liveMap.TryGetValue(ach.Id, out var livePick);
                ComingUpGrid.Children.Add(BuildBadgeTile(ach, median, livePick));
            }

            ComingUpSection.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Builds a single badge tile for the "Coming up" row: badge image
        /// (downloaded from RA's CDN), achievement title (truncated), and a
        /// caption — live progress text when this user was making in-game
        /// progress last session (e.g. "73% · 3 of 5"), otherwise the
        /// community median time-to-unlock. Hover-tooltip exposes the full
        /// description and point value.
        /// </summary>
        private UIElement BuildBadgeTile(RAAchievement ach, int medianSec, RALiveAchievementProgress? live)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // Badge — 56×56 image from media.retroachievements.org.
            var img = new System.Windows.Controls.Image
            {
                Width = 56,
                Height = 56,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            if (!string.IsNullOrEmpty(ach.BadgeName))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri($"https://media.retroachievements.org/Badge/{ach.BadgeName}.png");
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    img.Source = bmp;
                }
                catch { /* fall through to a blank tile */ }
            }
            panel.Children.Add(img);

            // Title (one line, truncated).
            var title = new TextBlock
            {
                Text = ach.Title,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 110,
                Margin = new Thickness(0, 4, 0, 0),
            };
            panel.Children.Add(title);

            // Caption — live progress wins when available, accent-tinted to
            // mark "this is your data, not community average." Falls back to
            // the community-median ETA, muted to mark it as an estimate.
            string captionText;
            Brush captionBrush;
            if (live != null && live.Percent > 0 && live.Percent < 100)
            {
                string pctStr = $"{live.Percent:0.#}%";
                captionText = string.IsNullOrEmpty(live.ProgressText)
                    ? pctStr
                    : $"{pctStr} · {live.ProgressText}";
                captionBrush = (Brush)FindResource("AccentBrush");
            }
            else
            {
                captionText = "~" + FormatDuration(medianSec);
                captionBrush = (Brush)FindResource("TextMutedBrush");
            }
            var caption = new TextBlock
            {
                Text = captionText,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 10,
                Foreground = captionBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            };
            panel.Children.Add(caption);

            // Tooltip: full description + rarity (% of all players) + points.
            // RA community calls "rarity" = (numAwarded / numPlayers); show the
            // softcore figure since users see all unlocks not just hardcore.
            var tipLines = new System.Text.StringBuilder();
            tipLines.AppendLine(ach.Title);
            if (!string.IsNullOrEmpty(ach.Description))
            {
                tipLines.AppendLine();
                tipLines.AppendLine(ach.Description);
            }
            tipLines.AppendLine();
            tipLines.Append($"{ach.Points} pts");
            panel.ToolTip = tipLines.ToString();

            return panel;
        }

        private void BuildTimingsCaption(RAProgression prog, bool hardcore)
        {
            // Sample-size gate — under n=20 the medians are too noisy to be
            // worth showing as a "typical run" estimate.
            const int MinSamples = 20;
            int? beatSec = hardcore ? (prog.MedianTimeToBeatHardcore ?? prog.MedianTimeToBeat) : prog.MedianTimeToBeat;
            string? beat = beatSec.HasValue && prog.TimesUsedInBeatMedian >= MinSamples
                ? FormatDuration(beatSec.Value) : null;
            string? master = prog.MedianTimeToMaster.HasValue
                          && prog.TimesUsedInMasteryMedian >= MinSamples
                ? FormatDuration(prog.MedianTimeToMaster.Value) : null;

            if (beat == null && master == null)
            {
                RATimingsCaption.Visibility = Visibility.Collapsed;
                return;
            }

            var parts = new List<string>();
            if (beat != null) parts.Add($"beat ~{beat}");
            if (master != null) parts.Add($"master ~{master}");
            RATimingsCaption.Text = "Typical run: " + string.Join("  ·  ", parts);
            RATimingsCaption.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Compact human-readable duration from a second count. Examples:
        ///   45  → "45s"
        ///   300 → "5m"
        ///   3720 → "1.0h"
        ///   100800 → "28h"
        /// Designed to fit a small pill / caption without wrapping.
        /// </summary>
        /// <summary>
        /// Console-aware message for the "rcheevos couldn't identify this
        /// ROM" case. The previous text universally suggested a "Redump"
        /// dump, which only makes sense for disc-based systems — for an
        /// Arcade ZIP or a cartridge ROM the hint is misleading. Splits
        /// into three buckets (arcade / disc / cart) keyed off the
        /// console tag.
        /// </summary>
        private static string UnrecognizedHashMessage(string console)
        {
            // Disc-based systems use Redump-style dumps. Anything that's
            // typically distributed as .cue/.bin/.chd/.iso/.gdi belongs here.
            // Source: project_segacd / project_saturn / project_3ds_status memories.
            switch (console)
            {
                case "Arcade":
                case "NeoGeo":
                    return "RetroAchievements doesn't recognize this ROM set — RA usually targets one specific parent or clone; check retroachievements.org to confirm the title is on RA and which set is supported";
                case "PS1":
                case "PS2":
                case "PSP":
                case "Saturn":
                case "Dreamcast":
                case "GameCube":
                case "SegaCD":
                case "TGCD":
                case "3DO":
                case "NeoCD":
                case "CDi":
                case "3DS":
                    return "RetroAchievements doesn't recognize this disc image — try a Redump-matching dump";
                default:
                    return "RetroAchievements doesn't recognize this ROM hash — try a No-Intro matching dump";
            }
        }

        private static string FormatDuration(int sec)
        {
            if (sec <= 0) return "—";
            if (sec < 60) return $"{sec}s";
            if (sec < 3600) return $"{sec / 60}m";
            double h = sec / 3600.0;
            return h < 100 ? $"{h:0.#}h" : $"{(int)h}h";
        }

        private async System.Threading.Tasks.Task ShowCoverArtPlaceholderAsync()
        {
            string artPath = _game.DisplayArtPath;
            if (string.IsNullOrEmpty(artPath) || !System.IO.File.Exists(artPath)) return;

            try
            {
                // BitmapImage decode (BeginInit→OnLoad→EndInit→Freeze) reads + decodes
                // the file synchronously on the calling thread. Frozen bitmaps cross
                // threads safely, so do the decode on a worker and only the assignment
                // on UI.
                var bitmap = await System.Threading.Tasks.Task.Run(() =>
                {
                    var bm = new BitmapImage();
                    bm.BeginInit();
                    bm.UriSource = new Uri(artPath, UriKind.Absolute);
                    bm.CacheOption = BitmapCacheOption.OnLoad;
                    bm.EndInit();
                    bm.Freeze();
                    return bm;
                });

                if (_closed) return;
                HeaderImage.Source = bitmap;
                HeaderImage.Stretch = Stretch.UniformToFill;
                HeaderImage.Visibility = Visibility.Visible;
                ArtPlaceholderText.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private async System.Threading.Tasks.Task PlaySnapVideoAsync(string mp4Path)
        {
            _crossfadeDone = false;

            // ── UI thread: bitmap + buffer MUST exist before any VLC display ──
            // callback can fire. The display callback marshals to UI and reads
            // _videoBitmap; if VLC fires before we've assigned it, early frames
            // drop silently and the placeholder flashes for ~30-100ms.
            //
            // ScreenScraper snaps are typically 320x240 — use fixed format
            _videoWidth = 320;
            _videoHeight = 240;
            int stride = _videoWidth * 4;

            if (_videoBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = Marshal.AllocHGlobal(stride * _videoHeight);

            _videoBitmap = new WriteableBitmap(_videoWidth, _videoHeight, 96, 96, PixelFormats.Bgr32, null);
            VideoImage.Source = _videoBitmap;

            // Capture by value for the worker / VLC callback closures.
            IntPtr bufferPtr = _videoBuffer;
            int width = _videoWidth, height = _videoHeight;

            // Awaits an already-completed Task on the hot path (warmed at app
            // startup). Only the very first call before warmup finishes can
            // suspend — and even then we yield back to the dispatcher instead
            // of blocking it.
            var libVLC = await Services.VideoPlaybackService.Instance.GetLibVLCAsync();

            // ── Worker thread: MediaPlayer ctor, callback wiring, Media open, Play ──
            await System.Threading.Tasks.Task.Run(() =>
            {
                var player = new LibVLCSharp.Shared.MediaPlayer(libVLC);
                player.SetVideoFormat("RV32", (uint)width, (uint)height, (uint)stride);

                player.SetVideoCallbacks(
                    // Lock: give VLC our buffer
                    (IntPtr opaque, IntPtr planes) =>
                    {
                        Marshal.WriteIntPtr(planes, bufferPtr);
                        return IntPtr.Zero;
                    },
                    // Unlock: no-op
                    null,
                    // Display: blit to WriteableBitmap
                    (IntPtr opaque, IntPtr picture) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (_videoBitmap == null || _videoBuffer == IntPtr.Zero) return;

                            _videoBitmap.Lock();
                            unsafe
                            {
                                Buffer.MemoryCopy(
                                    (void*)_videoBuffer, (void*)_videoBitmap.BackBuffer,
                                    stride * height, stride * height);
                            }
                            _videoBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                            _videoBitmap.Unlock();

                            // Crossfade once on first rendered frame
                            if (!_crossfadeDone)
                            {
                                _crossfadeDone = true;
                                VideoImage.Visibility = Visibility.Visible;
                                ArtPlaceholderText.Visibility = Visibility.Collapsed;
                                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                                fadeOut.Completed += (_, _) => HeaderImage.Visibility = Visibility.Collapsed;
                                HeaderImage.BeginAnimation(OpacityProperty, fadeOut);
                            }
                        });
                    });

                // Loop: when it ends, replay from the start
                player.EndReached += (_, _) =>
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => player.Play());

                using var media = new Media(libVLC, mp4Path, FromType.FromPath);
                media.AddOption(":input-repeat=65535");

                // Bail early if the dispatcher is gone — calling Invoke after
                // shutdown throws and the exception escapes Task.Run.
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    try { player.Dispose(); } catch { }
                    return;
                }

                // Stash AND start the player inside the UI-thread critical section.
                // OnClosed runs on UI too, so it can't interleave between the
                // assignment and Play — without that atomicity, OnClosed could
                // Dispose the player between the two and we'd Play on freed memory.
                bool keep = false;
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_closed) return;
                        _vlcPlayer = player;
                        player.Play(media);
                        keep = true;
                    });
                }
                catch (System.Threading.Tasks.TaskCanceledException) { }

                if (!keep)
                {
                    try { player.Dispose(); } catch { }
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            // Signal in-flight async work (placeholder decode, snap-video worker)
            // to drop its results instead of writing back into a dead window.
            _closed = true;

            // Cancel any in-flight RA refresh so its write-back to the Game
            // object can't race a fresh detail card opened immediately after.
            try { _raRefreshCts?.Cancel(); _raRefreshCts?.Dispose(); _raRefreshCts = null; }
            catch { }

            if (_vlcPlayer != null)
            {
                _vlcPlayer.Stop();
                _vlcPlayer.Dispose();
                _vlcPlayer = null;
            }

            // VLC's display callback marshals via Dispatcher.BeginInvoke, so
            // queued blit work items can outlive Stop()+Dispose() and run
            // briefly after this point. Zero the bail-out guards BEFORE freeing
            // the buffer so any in-flight callback sees them and returns instead
            // of memcpying into freed memory.
            _videoBitmap = null;
            var buf = _videoBuffer;
            _videoBuffer = IntPtr.Zero;
            if (buf != IntPtr.Zero)
                Marshal.FreeHGlobal(buf);

            base.OnClosed(e);
        }

        private void AnimateIn()
        {
            ModalCard.RenderTransform = new TranslateTransform(0, 30);
            ModalCard.Opacity = 0;

            var slideUp = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));

            ModalCard.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
            ModalCard.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void Overlay_Click(object sender, MouseButtonEventArgs e) => Close();
        private void CloseButton_Click(object sender, MouseButtonEventArgs e) => Close();

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            var coreManager = new CoreManager(App.Configuration!);

            // Check for missing BIOS before attempting to launch.
            string systemDir = AppPaths.GetFolder("System");
            string region = RomService.DetectRegion(_game.RomPath);
            string? romDir = System.IO.Path.GetDirectoryName(_game.RomPath);
            string? resolvedCore = coreManager.GetCorePathForGame(_game);
            var missingBios = CoreManager.GetMissingBios(_game.Console, systemDir, region,
                romDir != null ? new[] { romDir } : null, resolvedCore);
            if (missingBios.Count > 0)
            {
                var biosDialog = new BiosRequiredWindow(_game.Console, missingBios, region)
                    { Owner = this };
                biosDialog.ShowDialog();
                return;
            }

            // Games with an active enhancement pack/mod are pinned to a
            // pack-capable core (e.g. Mesen). When that core isn't installed,
            // GetCorePathForGame silently falls back to the console default and
            // the pack wouldn't render — ask first. (Mod set to None / pack
            // toggled off per-game → nothing to warn about.)
            if (Services.HdPackService.WantsPackCore(_game))
            {
                string preferredDll = Services.HdPackService.PreferredCoreFor(_game.Console);
                if (preferredDll.Length > 0 &&
                    !System.IO.File.Exists(System.IO.Path.Combine(AppPaths.GetCoresFolder(), preferredDll)))
                {
                    string coreLabel = System.IO.Path.GetFileNameWithoutExtension(preferredDll)
                        .Replace("_libretro", "");
                    var hdDialog = new ConfirmDialog("HD Pack",
                        $"This entry uses an HD pack that needs the '{coreLabel}' core, which isn't installed yet.\n\n" +
                        "Install it from Preferences → Cores, or play without the HD pack for now.",
                        "Play without HD pack", danger: false) { Owner = this };
                    if (hdDialog.ShowDialog() != true) return;
                }
            }

            // PS3: driven by an external emulator in its own process (no in-process libretro
            // core). Host its render window inside the app shell; report play time on exit.
            if (string.Equals(_game.Console, "PS3", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!Services.Ps3.Ps3Launch.EnsureReady(this)) return;
                var ps3Host = new Ps3HostWindow(_game);
                ps3Host.SessionEnded += secs => OnHostSessionEnded(secs);
                ps3Host.Show();
                return;
            }

            if (!coreManager.HasCore(_game.Console))
            {
                MessageBox.Show(
                    $"No emulator core found for {_game.Console}.\n\nMake sure the appropriate .dll core file is in the Cores folder next to the application.",
                    "Missing Core",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!System.IO.File.Exists(_game.RomPath))
            {
                bool wasTempExtracted = _game.RomPath.IndexOf(@"\Temp\Emutastic\",
                    System.StringComparison.OrdinalIgnoreCase) >= 0;

                string msg = wasTempExtracted
                    ? "This game was imported from a .zip and Windows has cleared its " +
                      "temporary working folder.\n\nRemove the entry from your library " +
                      "and re-import the original archive — newer imports stay persistent."
                    : $"ROM file not found:\n{_game.RomPath}";
                MessageBox.Show(msg,
                    wasTempExtracted ? "Re-import Required" : "File Not Found",
                    MessageBoxButton.OK,
                    wasTempExtracted ? MessageBoxImage.Warning : MessageBoxImage.Error);
                return;
            }

            try
            {
                string corePath = coreManager.GetCorePathForGame(_game)!;

                // PS2: run out-of-process (Emutastic.exe --emuhost). The clean child process
                // makes LRPS2 boot crash-free; the main app stays usable while the game runs.
                if (string.Equals(_game.Console, "PS2", System.StringComparison.OrdinalIgnoreCase))
                {
                    ChildHostLauncher.Launch(_game, corePath, null, secs => OnHostSessionEnded(secs));
                    return;
                }

                EmulatorWindow.FreeStaleDll(); // must be BEFORE LoadLibrary
                var core = new LibretroCore(corePath);
                var emulator = new EmulatorWindow(_game, core);
                emulator.ShowDialog();

                // The user just played — any cached per-user achievement state
                // is potentially stale. Mark it invalid so the next time the
                // detail card opens, fresh data is fetched. No network call
                // here — just a TTL stamp reset.
                if (App.Configuration != null)
                {
                    var ra = new RetroAchievementsService(App.Configuration, _db);
                    ra.InvalidateUserProgressForGame(_game);

                    // Also invalidate the Achievements tab's Library Spotlight
                    // + recent-unlocks cache so the user sees fresh "closest
                    // to mastering" / quick-wins right after a play session,
                    // not 15-min-stale data.
                    var raData = new RaDataService(App.Configuration, _db, ra);
                    raData.InvalidatePostPlay();
                }

                // Refresh stats — EmulatorWindow updates _game.PlayCount / LastPlayed / TotalPlayTimeSeconds
                // on the shared object, so the card shows accurate numbers immediately.
                if (IsVisible) RefreshStats();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to launch emulator:\n\n{ex.Message}",
                    "Launch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Called on the UI thread after an out-of-process (--emuhost) session ends. The child
        /// ran with a throwaway Game.Id so it didn't touch the DB; the parent ingests play-stats
        /// here, then mirrors the in-process post-play RA cache invalidation + stats refresh.
        /// </summary>
        private void OnHostSessionEnded(int playSeconds)
        {
            // The child process owns the DB writes (it runs with the real game id): play-stats,
            // save-state rows, per-game window size. Reload what it wrote so the card is current.
            try
            {
                var fresh = _db.GetGameById(_game.Id);
                if (fresh != null)
                {
                    _game.PlayCount = fresh.PlayCount;
                    _game.LastPlayed = fresh.LastPlayed;
                    _game.TotalPlayTimeSeconds = fresh.TotalPlayTimeSeconds;
                }
            }
            catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine($"[ChildHost] stats reload: {ex.Message}"); }

            if (App.Configuration != null)
            {
                var ra = new RetroAchievementsService(App.Configuration, _db);
                ra.InvalidateUserProgressForGame(_game);
                var raData = new RaDataService(App.Configuration, _db, ra);
                raData.InvalidatePostPlay();
            }

            if (IsVisible) RefreshStats();
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            _game.IsFavorite = !_game.IsFavorite;
            _db.ToggleFavorite(_game.Id, _game.IsFavorite);
            FavoriteButton.Content = _game.IsFavorite ? "♥  Favorited" : "♡  Favorite";
            FavoriteBadge.Visibility = _game.IsFavorite
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            var showInExplorer = new MenuItem { Header = "Show in Explorer" };
            showInExplorer.Click += (_, _) =>
            {
                if (System.IO.File.Exists(_game.RomPath))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_game.RomPath}\"");
            };

            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (_, _) =>
            {
                var dialog = new RenameWindow(_game.Title) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    _game.Title = dialog.NewTitle;
                    _game.TitleLocked = true;
                    _db.UpdateTitle(_game.Id, _game.Title, lockTitle: true);
                    GameTitle.Text = _game.Title;
                    ArtPlaceholderText.Text = _game.Title;
                }
            };

            var cheats = new MenuItem { Header = "Cheats…" };
            cheats.Click += (_, _) =>
            {
                var win = new CheatsManagerWindow(_game) { Owner = this };
                win.ShowDialog();
            };

            var remove = new MenuItem { Header = "Remove from Library" };
            remove.Click += (_, _) =>
            {
                var confirm = new ConfirmDialog(
                    "Remove Game",
                    $"Remove \"{_game.Title}\" from your library?\n\nThis will not delete the ROM file.",
                    "Remove",
                    danger: true) { Owner = this };
                if (confirm.ShowDialog() == true)
                {
                    _db.DeleteGame(_game.Id);
                    Close();
                }
            };

            menu.Items.Add(showInExplorer);
            menu.Items.Add(rename);

            var notes = new MenuItem { Header = "Notes…" };
            notes.Click += (_, _) => NotesWindow.ShowFor(_game, this);
            menu.Items.Add(notes);

            bool hasManual = _game.HasManual && System.IO.File.Exists(_game.ManualPath);
            var manual = new MenuItem { Header = hasManual ? "View Manual" : "Download Manual…" };
            manual.Click += async (_, _) =>
            {
                var fetch = (Application.Current.MainWindow as Emutastic.MainWindow)?.ArtworkFetch;
                if (fetch != null) await ManualLauncher.OpenOrDownloadAsync(_game, fetch, this);
                else if (hasManual) ManualViewerWindow.ShowFor(_game, this);
            };
            menu.Items.Add(manual);

            // Show the Cheats entry only when this console actually has a known core
            // AND that core isn't a known cheat-stub. Unknown consoles (no core in the
            // map) hide the entry — there's nothing to apply cheats against.
            if (Services.CoreManager.ConsoleCoreMap.TryGetValue(_game.Console ?? "", out var cores)
                && cores.Length > 0
                && Services.CheatSupport.Lookup(cores[0]).Level != Services.CheatSupportLevel.NotSupported)
            {
                menu.Items.Add(cheats);
            }

            // HD mods: the active pack is chosen HERE, before launch — never
            // mid-session (flipping packs at runtime crashes the stock Mesen
            // core; the next game start simply boots with the chosen mod).
            if (Services.HdPackService.IsMesenConsole(_game.Console ?? ""))
            {
                var (active, all) = Services.HdPackService.ListMods(_game);
                if (all.Count > 0)
                {
                    var hdRoot = new MenuItem { Header = "HD Mod" };
                    var none = new MenuItem
                    {
                        Header = "None",
                        IsCheckable = true,
                        IsChecked = active == null
                    };
                    none.Click += (_, _) => SetHdMod(null);
                    hdRoot.Items.Add(none);
                    foreach (var mod in all)
                    {
                        // Mesen 2 packs (format v107+) are silently ignored by the
                        // classic core — show them, but say why they can't be used.
                        int ver = Services.HdPackService.GetModVersion(_game, mod);
                        bool unsupported = ver > Services.HdPackService.MaxSupportedPackVersion;
                        var item = new MenuItem
                        {
                            Header = unsupported ? $"{mod}  (needs Mesen 2 — unsupported)" : mod,
                            IsCheckable = true,
                            IsEnabled = !unsupported,
                            IsChecked = string.Equals(mod, active, StringComparison.OrdinalIgnoreCase)
                        };
                        string captured = mod;
                        item.Click += (_, _) => SetHdMod(captured);
                        hdRoot.Items.Add(item);
                    }

                    hdRoot.Items.Add(new Separator());
                    var renameRoot = new MenuItem { Header = "Rename Mod" };
                    foreach (var mod in all)
                    {
                        var r = new MenuItem { Header = mod };
                        string captured = mod;
                        r.Click += (_, _) =>
                        {
                            var dlg = new RenameWindow(captured) { Owner = this };
                            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.NewTitle)
                                && !string.Equals(dlg.NewTitle, captured, StringComparison.Ordinal))
                            {
                                if (!Services.HdPackService.RenameMod(_game, captured, dlg.NewTitle))
                                    MessageBox.Show(this,
                                        "Couldn't rename the mod — the name may already exist, or its files are in use.",
                                        "HD Mod", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        };
                        renameRoot.Items.Add(r);
                    }
                    hdRoot.Items.Add(renameRoot);
                    menu.Items.Add(hdRoot);
                }
            }
            else if (Services.HdPackService.IsTexturePackConsole(_game.Console ?? "") && _game.HasHdPack)
            {
                var texToggle = new MenuItem
                {
                    Header = "Texture Pack",
                    IsCheckable = true,
                    IsChecked = _game.HdPackEnabled
                };
                texToggle.Click += (_, _) =>
                {
                    _game.HdPackEnabled = !_game.HdPackEnabled;
                    _db.UpdateHdPackEnabled(_game.Id, _game.HdPackEnabled);
                };
                menu.Items.Add(texToggle);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(remove);

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Applies the chosen HD mod on disk (folder rename). Off the UI thread —
        // packs can be large. Fails cleanly if the game is running and holds
        // pack files open (music streams); the user closes it and re-picks.
        private async void SetHdMod(string? modName)
        {
            bool ok = await System.Threading.Tasks.Task.Run(
                () => Services.HdPackService.ActivateMod(_game, modName));
            if (!ok)
            {
                MessageBox.Show(this,
                    "Couldn't switch the HD mod — if the game is running, close it and try again.",
                    "HD Mod", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
