using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views
{
    /// <summary>
    /// EmuTV — the controller-first, 10-foot "couch" shell for Emutastic.
    ///
    /// Increment 1c + TV preview: system carousel → A opens a gamelist (left) with
    /// a CRT "TV" preview (right) that plays the selected game's cached video snap
    /// over the screen, debounced as you scroll → A launches via the SAME path the
    /// desktop uses → quitting returns here.
    ///
    /// IP/bundling policy: bundles NO third-party art — console tiles are our own
    /// neon cards, the TV frame is our own image, and game art / video snaps are
    /// the user's own library media (per-user, not redistributed by us).
    /// </summary>
    public partial class EmuTvWindow : Window
    {
        private enum NavMode { Carousel, GameList }

        private readonly ControllerManager? _controller;
        private readonly DatabaseService? _db;
        private readonly DispatcherTimer? _inputTimer;

        private NavMode _mode = NavMode.Carousel;
        private bool _bLatch;
        private bool _aLatch;
        private int  _navDir;
        private int  _navHoldTicks;

        private const int NavRepeatDelayTicks = 4; // ~240ms before auto-repeat
        private const int NavRepeatRateTicks  = 2; // repeat every ~120ms while held

        // ── TV video (VLC frames → WriteableBitmap, composited over the TV PNG) ──
        private readonly DispatcherTimer _videoDebounce;
        private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;
        private WriteableBitmap? _videoBitmap;
        private IntPtr _videoBuffer;
        private bool _crossfadeDone;
        private bool _closed;

        public EmuTvWindow(ControllerManager? controller = null, DatabaseService? db = null)
        {
            InitializeComponent();
            _controller = controller;
            _db = db;

            PreviewKeyDown += OnPreviewKeyDown;
            SystemCarousel.SelectionChanged += (_, _) => OnCarouselSelectionChanged();
            GameList.SelectionChanged += (_, _) => OnGameSelectionChanged();
            Loaded += (_, _) => LoadConsoles();
            UpdateHint();

            // Only spin up VLC once the selection settles, so fast scrolling shows
            // instant box art (the bound fallback) without thrashing the player.
            _videoDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _videoDebounce.Tick += OnVideoDebounceTick;

            if (_controller != null)
            {
                _inputTimer = new DispatcherTimer(DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(60)
                };
                _inputTimer.Tick += OnInputTick;
                _inputTimer.Start();
            }
        }

        // ── Data ──────────────────────────────────────────────────────────────
        private void LoadConsoles()
        {
            var db = _db;
            Task.Run(() =>
            {
                List<ConsoleGroup> groups;
                try
                {
                    var all = db?.GetAllGames() ?? new List<Game>();
                    groups = all
                        .Where(g => !string.IsNullOrWhiteSpace(g.Console))
                        .GroupBy(g => g.Console)
                        .Select(grp => new ConsoleGroup
                        {
                            ConsoleName = grp.Key,
                            TotalCount  = grp.Count(),
                            Games       = new ObservableCollection<Game>(
                                              grp.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
                        })
                        .OrderByDescending(cg => cg.TotalCount)
                        .ThenBy(cg => cg.ConsoleName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch
                {
                    groups = new List<ConsoleGroup>();
                }

                Dispatcher.Invoke(() =>
                {
                    SystemCarousel.ItemsSource = groups;
                    if (groups.Count > 0)
                    {
                        StatusLabel.Visibility = Visibility.Collapsed;
                        SystemCarousel.SelectedIndex = 0;
                        OnCarouselSelectionChanged();
                    }
                    else
                    {
                        StatusLabel.Text = "No games in your library yet.";
                    }
                });
            });
        }

        // ── Carousel ──────────────────────────────────────────────────────────
        private void OnCarouselSelectionChanged()
        {
            SelectedConsoleLabel.Text = (SystemCarousel.SelectedItem as ConsoleGroup)?.ConsoleName ?? "";
            CenterSelected();
        }

        private TranslateTransform? _carouselShift;

        // Keep the selected console centered by sliding the items panel (the
        // CarouselShift TranslateTransform in the ListBox template). Deterministic —
        // it directly moves the strip, so the selection always stays centered
        // (the ScrollViewer approach wasn't following). Deferred to Loaded priority
        // so the containers + ActualWidth are realized first.
        private void CenterSelected()
        {
            if (SystemCarousel.SelectedIndex < 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _carouselShift ??= SystemCarousel.Template?.FindName("CarouselShift", SystemCarousel) as TranslateTransform;
                if (_carouselShift == null) return;
                double pitch = GetCarouselItemPitch();
                double vw = SystemCarousel.ActualWidth;
                if (pitch <= 0 || vw <= 0) return;
                double target = vw / 2.0 - (SystemCarousel.SelectedIndex * pitch + pitch / 2.0);
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    target, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                _carouselShift.BeginAnimation(TranslateTransform.XProperty, anim);
            }));
        }

        private double GetCarouselItemPitch()
        {
            if (SystemCarousel.ItemContainerGenerator.ContainerFromIndex(
                    Math.Max(0, SystemCarousel.SelectedIndex)) is ListBoxItem it && it.ActualWidth > 0)
                return it.ActualWidth + it.Margin.Left + it.Margin.Right;
            return 340; // 300 tile width + 20+20 margin (matches the XAML)
        }

        // ── Mode transitions ────────────────────────────────────────────────────
        private void OpenSelectedConsole()
        {
            if (SystemCarousel.SelectedItem is not ConsoleGroup cg || cg.Games.Count == 0) return;

            // Switch to gamelist mode FIRST so the SelectedIndex change below arms
            // the video debounce (which only runs while mode == GameList).
            _mode = NavMode.GameList;
            CarouselPanel.Visibility = Visibility.Collapsed;
            GameListPanel.Visibility = Visibility.Visible;
            UpdateHint();

            GameList.ItemsSource = cg.Games;
            GameListHeader.Text  = $"{cg.ConsoleName}  ·  {cg.TotalCount} games";
            GameList.SelectedIndex = 0;
            if (GameList.Items.Count > 0) GameList.ScrollIntoView(GameList.Items[0]);
            OnGameSelectionChanged(); // guarantee the preview arms even if index 0 was unchanged
        }

        private void BackToCarousel()
        {
            StopVideo();
            _videoDebounce.Stop();
            _mode = NavMode.Carousel;
            GameListPanel.Visibility = Visibility.Collapsed;
            CarouselPanel.Visibility = Visibility.Visible;
            UpdateHint();
        }

        private void UpdateHint() =>
            HintLabel.Text = _mode == NavMode.Carousel
                ? "◀ ▶  Navigate      A  Open      B  Back"
                : "▲ ▼  Navigate      A  Play      B  Back";

        private void OnAccept()
        {
            if (_mode == NavMode.Carousel) OpenSelectedConsole();
            else if (GameList.SelectedItem is Game g) LaunchGame(g);
        }

        private void OnBack()
        {
            if (_mode == NavMode.GameList) BackToCarousel();
            else Close();
        }

        // ── TV preview video ────────────────────────────────────────────────────
        // Selection changed: drop the current clip (the bound box art shows the new
        // game instantly), then re-arm the debounce to play the new one once settled.
        private void OnGameSelectionChanged()
        {
            StopVideo();
            _videoDebounce.Stop();
            if (_mode == NavMode.GameList && GameList.SelectedItem is Game)
                _videoDebounce.Start();
        }

        private void OnVideoDebounceTick(object? sender, EventArgs e)
        {
            _videoDebounce.Stop();
            if (_closed || _mode != NavMode.GameList) return;
            if (GameList.SelectedItem is Game g) _ = TryPlayVideoForAsync(g);
        }

        private async Task TryPlayVideoForAsync(Game g)
        {
            try
            {
                var ss = new ScreenScraperService();
                string? path = ss.FindCachedSnap(g.RomHash, g.Console);

                if (string.IsNullOrEmpty(path))
                {
                    // Not cached yet — auto-download in the background if ScreenScraper
                    // is configured, the same way the desktop detail card does. Saves
                    // users from opening every game manually to seed snaps. (No hash →
                    // nothing to look up.)
                    var snap = App.Configuration?.GetSnapConfiguration();
                    if (snap is { ScreenScraperEnabled: true }
                        && !string.IsNullOrWhiteSpace(snap.ScreenScraperUser)
                        && !string.IsNullOrWhiteSpace(g.RomHash))
                    {
                        SetTvDownloading(true);
                        try
                        {
                            path = await ss.FetchSnapAsync(
                                snap.ScreenScraperUser, snap.ScreenScraperPassword,
                                g.Console, g.RomHash, g.RomPath);
                        }
                        finally
                        {
                            if (ReferenceEquals(GameList.SelectedItem, g)) SetTvDownloading(false);
                        }
                    }
                }

                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

                // Selection may have moved during a download — only play if still current.
                if (_closed || _mode != NavMode.GameList
                    || !ReferenceEquals(GameList.SelectedItem, g)) return;

                await PlayTvVideoAsync(path, g);
            }
            catch { /* cosmetic */ }
        }

        private void SetTvDownloading(bool on) =>
            TvStandByImage.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        private async Task PlayTvVideoAsync(string mp4Path, Game forGame)
        {
            _crossfadeDone = false;

            const int w = 320, h = 240;       // ScreenScraper snaps are 320x240
            int stride = w * 4;

            if (_videoBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = Marshal.AllocHGlobal(stride * h);
            _videoBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            TvVideoImage.Source = _videoBitmap;

            IntPtr bufferPtr = _videoBuffer;

            var libVLC = await VideoPlaybackService.Instance.GetLibVLCAsync();

            await Task.Run(() =>
            {
                var player = new LibVLCSharp.Shared.MediaPlayer(libVLC);
                player.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)stride);

                player.SetVideoCallbacks(
                    (IntPtr opaque, IntPtr planes) => { Marshal.WriteIntPtr(planes, bufferPtr); return IntPtr.Zero; },
                    null,
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
                                    (long)stride * h, (long)stride * h);
                            }
                            _videoBitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
                            _videoBitmap.Unlock();

                            if (!_crossfadeDone)
                            {
                                _crossfadeDone = true;
                                TvVideoImage.Visibility = Visibility.Visible;
                            }
                        });
                    });

                player.EndReached += (_, _) =>
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { player.Play(); } catch { } });

                using var media = new LibVLCSharp.Shared.Media(libVLC, mp4Path, LibVLCSharp.Shared.FromType.FromPath);
                media.AddOption(":input-repeat=65535");

                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    try { player.Dispose(); } catch { }
                    return;
                }

                bool keep = false;
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        // Selection may have moved while we were spinning up — only
                        // commit if this is still the chosen game and we're still live.
                        if (_closed || _mode != NavMode.GameList
                            || !ReferenceEquals(GameList.SelectedItem, forGame)) return;
                        _vlcPlayer = player;
                        player.Play(media);
                        keep = true;
                    });
                }
                catch (TaskCanceledException) { }

                if (!keep) { try { player.Dispose(); } catch { } }
            });
        }

        private void StopVideo()
        {
            var p = _vlcPlayer;
            _vlcPlayer = null;
            if (p != null)
            {
                try { p.Stop(); } catch { }
                try { p.Dispose(); } catch { }
            }
            _crossfadeDone = false;
            TvVideoImage.Visibility = Visibility.Collapsed; // fall back to the bound box art
            TvStandByImage.Visibility = Visibility.Collapsed;
        }

        // ── Launch (reuses the desktop path exactly) ────────────────────────────
        private void LaunchGame(Game game)
        {
            try
            {
                var coreManager = new CoreManager(App.Configuration!);
                if (!coreManager.HasCore(game.Console))
                {
                    MessageBox.Show(this,
                        $"No emulator core found for {game.Console}.\n\nInstall it via Preferences → Cores.",
                        "Missing Core", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!System.IO.File.Exists(game.RomPath))
                {
                    MessageBox.Show(this,
                        $"ROM file not found:\n{game.RomPath}",
                        "File Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Free the preview's VLC + pause our input polling while the game runs.
                StopVideo();
                _videoDebounce.Stop();
                _inputTimer?.Stop();

                string corePath = coreManager.GetCorePathForGame(game)!;
                EmulatorWindow.FreeStaleDll(); // must be BEFORE LoadLibrary
                var core = new LibretroCore(corePath);
                var emulator = new EmulatorWindow(game, core) { Owner = this, StartInFullscreen = true };

                try { emulator.ShowDialog(); }
                finally
                {
                    // Require a fresh A/B press before they act again (buttons may
                    // still be held from the in-game quit chord), then replay the
                    // current selection's preview.
                    _aLatch = true;
                    _bLatch = true;
                    _navDir = 0;
                    _navHoldTicks = 0;
                    _inputTimer?.Start();
                    OnGameSelectionChanged();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to launch:\n{ex.Message}",
                    "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _inputTimer?.Start();
            }
        }

        // ── Input ────────────────────────────────────────────────────────────────
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                case Key.Back:  e.Handled = true; OnBack(); break;
                case Key.Enter: e.Handled = true; OnAccept(); break;
                case Key.Left:  if (_mode == NavMode.Carousel) { e.Handled = true; MoveCarousel(-1); } break;
                case Key.Right: if (_mode == NavMode.Carousel) { e.Handled = true; MoveCarousel(1);  } break;
                case Key.Up:    if (_mode == NavMode.GameList)  { e.Handled = true; MoveGameList(-1); } break;
                case Key.Down:  if (_mode == NavMode.GameList)  { e.Handled = true; MoveGameList(1);  } break;
            }
        }

        private void OnInputTick(object? sender, EventArgs e)
        {
            if (_controller == null) return;

            bool b = _controller.IsRawXInputButtonDown(ControllerManager.RAW_B);
            if (b && !_bLatch) { _bLatch = true; OnBack(); return; }
            if (!b) _bLatch = false;

            bool a = _controller.IsRawXInputButtonDown(ControllerManager.RAW_A);
            if (a && !_aLatch) { _aLatch = true; OnAccept(); return; }
            if (!a) _aLatch = false;

            int dir;
            if (_mode == NavMode.Carousel)
            {
                bool l = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_LEFT)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_LEFT);
                bool r = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_RIGHT)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_RIGHT);
                dir = r ? 1 : l ? -1 : 0;
            }
            else
            {
                bool u = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_UP)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_UP);
                bool d = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_DOWN)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_DOWN);
                dir = d ? 1 : u ? -1 : 0;
            }

            if (dir == 0) { _navDir = 0; _navHoldTicks = 0; return; }

            if (dir != _navDir)
            {
                _navDir = dir;
                _navHoldTicks = 0;
                ApplyMove(dir);
            }
            else
            {
                _navHoldTicks++;
                if (_navHoldTicks >= NavRepeatDelayTicks &&
                    (_navHoldTicks - NavRepeatDelayTicks) % NavRepeatRateTicks == 0)
                    ApplyMove(dir);
            }
        }

        private void ApplyMove(int dir)
        {
            if (_mode == NavMode.Carousel) MoveCarousel(dir);
            else MoveGameList(dir);
        }

        private void MoveCarousel(int delta)
        {
            int n = SystemCarousel.Items.Count;
            if (n == 0) return;
            int i = Math.Clamp(SystemCarousel.SelectedIndex + delta, 0, n - 1);
            if (i != SystemCarousel.SelectedIndex) SystemCarousel.SelectedIndex = i;
        }

        private void MoveGameList(int delta)
        {
            int n = GameList.Items.Count;
            if (n == 0) return;
            int i = Math.Clamp(GameList.SelectedIndex + delta, 0, n - 1);
            if (i != GameList.SelectedIndex)
            {
                GameList.SelectedIndex = i;
                GameList.ScrollIntoView(GameList.Items[i]);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _inputTimer?.Stop();
            _videoDebounce.Stop();
            StopVideo();

            // Display callbacks marshal via BeginInvoke and can outlive Stop(); zero
            // the guards before freeing so any in-flight blit bails instead of writing
            // into freed memory.
            _videoBitmap = null;
            var buf = _videoBuffer;
            _videoBuffer = IntPtr.Zero;
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);

            base.OnClosed(e);
        }

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
