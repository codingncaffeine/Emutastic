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

        // Shared LibVLC instance — expensive to create, reused across all detail windows
        private static LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;
        private WriteableBitmap? _videoBitmap;
        private IntPtr _videoBuffer;
        private int _videoWidth, _videoHeight;
        private bool _crossfadeDone;

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
        /// Renders the inline stats pills (Times Played / Save States / Last
        /// Played) alongside the meta pills. Each pill hides when its value
        /// is zero/Never, so the right cluster doesn't get noisy for a freshly
        /// imported game.
        /// </summary>
        private void UpdateStatPills()
        {
            int plays = _game.PlayCount;
            int saves = _game.SaveCount;
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

            if (saves > 0)
            {
                StatSaves.Text = saves == 1 ? "1 save" : $"{saves} saves";
                SavesPill.Visibility = Visibility.Visible;
            }
            else
            {
                SavesPill.Visibility = Visibility.Collapsed;
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
                // Show cover art immediately as a placeholder while video loads
                ShowCoverArtPlaceholder();

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
                        Dispatcher.Invoke(() => PlaySnapVideo(cached));
                        return;
                    }
                }

                // 2 — fall back to static libretro screenshot
                var artworkService = new ArtworkService();
                string? snapPath = await artworkService.FetchSnapAsync(
                    _game.RomHash, _game.RomPath, _game.Console);

                if (snapPath == null || !System.IO.File.Exists(snapPath)) return;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(snapPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                Dispatcher.Invoke(() =>
                {
                    HeaderImage.Source = bitmap;
                    HeaderImage.Visibility = Visibility.Visible;
                    ArtPlaceholderText.Visibility = Visibility.Collapsed;
                });
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
            if (_game.RAGameId <= 0)
            {
                RASection.Visibility = Visibility.Collapsed;
                return;
            }

            // Render whatever's already cached so the section appears instantly.
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

            // No progression data yet — show the section as a skeleton-less
            // hidden state until the first fetch lands.
            if (prog == null || prog.NumAchievements <= 0)
            {
                RASection.Visibility = Visibility.Collapsed;
                return;
            }

            RASection.Visibility = Visibility.Visible;

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

            // Phase-1 picker: unearned, sorted ascending by community median
            // time-to-unlock (= typical players unlock these fastest), tiebreak
            // descending numAwarded (= more popular first), take 3. Skip any
            // achievement with a null/zero median — that's a no-data signal,
            // not "instant." Hardcore mode uses the hardcore-flavoured median
            // and unlock count when available.
            var picks = prog.Achievements
                .Where(a => !earnedIds.Contains(a.Id))
                .Select(a => new
                {
                    Ach = a,
                    Median = hardcore ? (a.MedianTimeToUnlockHardcore ?? a.MedianTimeToUnlock) : a.MedianTimeToUnlock,
                    Pop    = hardcore ? a.NumAwardedHardcore : a.NumAwarded,
                })
                .Where(x => x.Median.HasValue && x.Median.Value > 0)
                .OrderBy(x => x.Median!.Value)
                .ThenByDescending(x => x.Pop)
                .Take(3)
                .Select(x => x.Ach)
                .ToList();

            if (picks.Count == 0)
            {
                ComingUpSection.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var ach in picks)
            {
                int median = (hardcore ? (ach.MedianTimeToUnlockHardcore ?? ach.MedianTimeToUnlock) : ach.MedianTimeToUnlock) ?? 0;
                ComingUpGrid.Children.Add(BuildBadgeTile(ach, median));
            }

            ComingUpSection.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Builds a single badge tile for the "Coming up" row: badge image
        /// (downloaded from RA's CDN), achievement title (truncated), and
        /// median time-to-unlock caption. Hover-tooltip exposes the full
        /// description, points, and community rarity.
        /// </summary>
        private UIElement BuildBadgeTile(RAAchievement ach, int medianSec)
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

            // Time-to-unlock caption.
            var caption = new TextBlock
            {
                Text = "~" + FormatDuration(medianSec),
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
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
        private static string FormatDuration(int sec)
        {
            if (sec <= 0) return "—";
            if (sec < 60) return $"{sec}s";
            if (sec < 3600) return $"{sec / 60}m";
            double h = sec / 3600.0;
            return h < 100 ? $"{h:0.#}h" : $"{(int)h}h";
        }

        private void ShowCoverArtPlaceholder()
        {
            string artPath = _game.DisplayArtPath;
            if (string.IsNullOrEmpty(artPath) || !System.IO.File.Exists(artPath)) return;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(artPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                HeaderImage.Source = bitmap;
                HeaderImage.Stretch = Stretch.UniformToFill;
                HeaderImage.Visibility = Visibility.Visible;
                ArtPlaceholderText.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void PlaySnapVideo(string mp4Path)
        {
            _libVLC ??= new LibVLC("--no-audio", "--no-osd", "--no-snapshot-preview");
            _crossfadeDone = false;

            // ScreenScraper snaps are typically 320x240 — use fixed format
            _videoWidth = 320;
            _videoHeight = 240;
            int stride = _videoWidth * 4;

            if (_videoBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = Marshal.AllocHGlobal(stride * _videoHeight);

            _videoBitmap = new WriteableBitmap(_videoWidth, _videoHeight, 96, 96, PixelFormats.Bgr32, null);
            VideoImage.Source = _videoBitmap;

            _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            _vlcPlayer.SetVideoFormat("RV32", (uint)_videoWidth, (uint)_videoHeight, (uint)stride);

            _vlcPlayer.SetVideoCallbacks(
                // Lock: give VLC our buffer
                (IntPtr opaque, IntPtr planes) =>
                {
                    Marshal.WriteIntPtr(planes, _videoBuffer);
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
                                stride * _videoHeight, stride * _videoHeight);
                        }
                        _videoBitmap.AddDirtyRect(new Int32Rect(0, 0, _videoWidth, _videoHeight));
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
            _vlcPlayer.EndReached += (_, _) =>
                System.Threading.ThreadPool.QueueUserWorkItem(_ => _vlcPlayer?.Play());

            using var media = new Media(_libVLC, mp4Path, FromType.FromPath);
            media.AddOption(":input-repeat=65535");
            _vlcPlayer.Play(media);
        }

        protected override void OnClosed(EventArgs e)
        {
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
            if (_videoBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_videoBuffer);
                _videoBuffer = IntPtr.Zero;
            }
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
                }

                // Refresh stats — EmulatorWindow updates _game.PlayCount / LastPlayed / SaveCount
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
                    _db.UpdateTitle(_game.Id, _game.Title);
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

            // Show the Cheats entry only when this console actually has a known core
            // AND that core isn't a known cheat-stub. Unknown consoles (no core in the
            // map) hide the entry — there's nothing to apply cheats against.
            if (Services.CoreManager.ConsoleCoreMap.TryGetValue(_game.Console ?? "", out var cores)
                && cores.Length > 0
                && Services.CheatSupport.Lookup(cores[0]).Level != Services.CheatSupportLevel.NotSupported)
            {
                menu.Items.Add(cheats);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(remove);

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
