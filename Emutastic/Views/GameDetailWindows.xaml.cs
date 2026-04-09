using Emutastic.Configuration;
using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Views;
using LibVLCSharp.Shared;
using System;
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
        }

        private void PopulateData()
        {
            GameTitle.Text = _game.Title;
            GameYear.Text = _game.Year > 0 ? _game.Year.ToString() : "";
            ConsoleTag.Text = _game.Console;
            ArtPlaceholderText.Text = _game.Title;
            StatPlayed.Text = _game.PlayCount.ToString();
            StatSaves.Text = _game.SaveCount.ToString();
            StatLastPlayed.Text = _game.LastPlayedDisplay;
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
            StatPlayed.Text = _game.PlayCount.ToString();
            StatSaves.Text = _game.SaveCount.ToString();
            StatLastPlayed.Text = _game.LastPlayedDisplay;
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
                    string? cached = ss.FindCachedSnap(_game.RomHash);
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
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string systemDir = System.IO.Path.Combine(appData, "Emutastic", "System");
            string region = RomService.DetectRegion(_game.RomPath);
            string? romDir = System.IO.Path.GetDirectoryName(_game.RomPath);
            string? resolvedCore = coreManager.GetCorePath(_game.Console);
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
                MessageBox.Show(
                    $"ROM file not found:\n{_game.RomPath}",
                    "File Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            try
            {
                string corePath = coreManager.GetCorePath(_game.Console)!;
                var core = new LibretroCore(corePath);
                var emulator = new EmulatorWindow(_game, core);
                emulator.ShowDialog();

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
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
