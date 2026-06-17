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
    /// Three nav zones in the gamelist view:
    ///   • Carousel   — consoles (Left/Right), A opens a console.
    ///   • GameList   — games (Up/Down), A launches fresh, Right enters the saves.
    ///   • SaveStates — this game's save states (Left/Right), A loads the save,
    ///                  Left past the first returns to the game list.
    ///
    /// IP/bundling policy: bundles only our own art (emuTV/CRT/VCR/test-card);
    /// game art, video snaps and save thumbnails are the user's own library media.
    /// </summary>
    public partial class EmuTvWindow : Window
    {
        private enum NavMode { Carousel, GameList, SaveStates }

        private readonly ControllerManager? _controller;
        private readonly DatabaseService? _db;
        private readonly DispatcherTimer? _inputTimer;

        private NavMode _mode = NavMode.Carousel;
        private bool _bLatch;
        private bool _aLatch;
        private bool _rightLatch; // edge for GameList → SaveStates
        private int  _navDir;
        private int  _navHoldTicks;

        private const int NavRepeatDelayTicks = 4; // ~240ms before auto-repeat
        private const int NavRepeatRateTicks  = 2; // repeat every ~120ms while held

        // L1/R1 page-jump through the gamelist, accelerating on hold.
        private int  _pageDir;
        private int  _pageHoldTicks;
        private const int PageRepeatDelayTicks = 5; // ~300ms before auto-repeat
        private const int PageRepeatStartTicks = 3; // initial repeat interval (~180ms)
        private const int PageRepeatMinTicks   = 1; // fastest repeat (~60ms)
        private const int PageRampEveryTicks   = 6; // shrink interval every N held ticks

        // ── TV video ──
        private readonly DispatcherTimer _videoDebounce;
        private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;
        private WriteableBitmap? _videoBitmap;
        private IntPtr _videoBuffer;
        private bool _crossfadeDone;
        private bool _closed;
        private int  _videoGen; // bumped on every stop/selection change to cancel in-flight video work

        public EmuTvWindow(ControllerManager? controller = null, DatabaseService? db = null)
        {
            InitializeComponent();
            _controller = controller;
            _db = db;

            PreviewKeyDown += OnPreviewKeyDown;
            SystemCarousel.SelectionChanged += (_, _) => OnCarouselSelectionChanged();
            GameList.SelectionChanged += (_, _) => OnGameSelectionChanged();
            SaveList.SelectionChanged += (_, _) => CenterSaveSelected();
            Loaded += (_, _) => LoadConsoles();
            UpdateHint();

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

        // ── Data: consoles ──────────────────────────────────────────────────────
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
                catch { groups = new List<ConsoleGroup>(); }

                Dispatcher.Invoke(() =>
                {
                    SystemCarousel.ItemsSource = groups;
                    if (groups.Count > 0)
                    {
                        StatusLabel.Visibility = Visibility.Collapsed;
                        SystemCarousel.SelectedIndex = 0;
                        OnCarouselSelectionChanged();
                    }
                    else StatusLabel.Text = "No games in your library yet.";
                });
            });
        }

        // ── Carousel (consoles) ─────────────────────────────────────────────────
        private void OnCarouselSelectionChanged()
        {
            SelectedConsoleLabel.Text = (SystemCarousel.SelectedItem as ConsoleGroup)?.ConsoleName ?? "";
            CenterShift(SystemCarousel, ref _carouselShift, "CarouselShift", 340);
        }

        private TranslateTransform? _carouselShift;
        private TranslateTransform? _saveShift;

        // Slide a horizontal carousel's items panel so the selected item is centered.
        private void CenterShift(ListBox list, ref TranslateTransform? cached, string transformName, double pitch)
        {
            if (list.SelectedIndex < 0) return;
            var captured = cached;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                captured ??= list.Template?.FindName(transformName, list) as TranslateTransform;
                if (captured == null) return;
                double vw = list.ActualWidth;
                if (vw <= 0) return;
                double target = vw / 2.0 - (list.SelectedIndex * pitch + pitch / 2.0);
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    target, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                captured.BeginAnimation(TranslateTransform.XProperty, anim);
            }));
            // Cache the resolved transform for next time.
            if (cached == null)
                cached = list.Template?.FindName(transformName, list) as TranslateTransform;
        }

        private void CenterSaveSelected() => CenterShift(SaveList, ref _saveShift, "SaveShift", 252);

        // ── Mode transitions ────────────────────────────────────────────────────
        private void OpenSelectedConsole()
        {
            if (SystemCarousel.SelectedItem is not ConsoleGroup cg || cg.Games.Count == 0) return;

            _mode = NavMode.GameList;
            CarouselPanel.Visibility = Visibility.Collapsed;
            GameListPanel.Visibility = Visibility.Visible;
            GameList.Opacity = 1.0;
            SaveRow.Opacity = 0.5;
            UpdateHint();

            GameList.ItemsSource = cg.Games;
            GameListHeader.Text  = $"{cg.ConsoleName}  ·  {cg.TotalCount} games";
            GameList.SelectedIndex = 0;
            if (GameList.Items.Count > 0) GameList.ScrollIntoView(GameList.Items[0]);
            OnGameSelectionChanged();
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

        private void EnterSaveStates()
        {
            if (_mode != NavMode.GameList || SaveList.Items.Count == 0) return;
            _mode = NavMode.SaveStates;
            if (SaveList.SelectedIndex < 0) SaveList.SelectedIndex = 0;
            GameList.Opacity = 0.5;
            SaveRow.Opacity = 1.0;
            UpdateHint();
            // Consume the Right that brought us here so a continued hold doesn't
            // immediately cycle off the first save.
            _navDir = 1;
            _navHoldTicks = 0;
        }

        private void ExitSaveStatesToList()
        {
            _mode = NavMode.GameList;
            GameList.Opacity = 1.0;
            SaveRow.Opacity = 0.5;
            UpdateHint();
            _navDir = 0;
            _navHoldTicks = 0;
        }

        private void UpdateHint() =>
            HintLabel.Text = _mode switch
            {
                NavMode.Carousel   => "◀ ▶  Navigate      A  Open      B  Back",
                NavMode.GameList   => "▲ ▼  Games      ▶  Save states      A  Play      B  Back",
                _                  => "◀ ▶  Save states      A  Load      ◀  Back to games",
            };

        private void OnAccept()
        {
            if (_mode == NavMode.Carousel) OpenSelectedConsole();
            else if (_mode == NavMode.GameList && GameList.SelectedItem is Game g) LaunchGame(g);
            else if (_mode == NavMode.SaveStates
                     && GameList.SelectedItem is Game game
                     && SaveList.SelectedItem is SaveState s)
                LaunchGame(game, s.StatePath);
        }

        private void OnBack()
        {
            switch (_mode)
            {
                case NavMode.SaveStates: ExitSaveStatesToList(); break;
                case NavMode.GameList:   BackToCarousel(); break;
                default:                 Close(); break;
            }
        }

        // ── Save states ──────────────────────────────────────────────────────────
        // Load the selected game's saves OFF the UI thread, then bind if still current.
        private void LoadSavesFor(Game g)
        {
            var db = _db;
            Task.Run(() =>
            {
                List<SaveState> saves;
                try { saves = db?.GetSaveStatesByGame(g.Id) ?? new List<SaveState>(); }
                catch { saves = new List<SaveState>(); }

                Dispatcher.Invoke(() =>
                {
                    if (_closed || !ReferenceEquals(GameList.SelectedItem, g)) return;
                    SaveList.ItemsSource = saves;
                    NoSavesLabel.Visibility = saves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (saves.Count > 0) SaveList.SelectedIndex = 0;
                });
            });
        }

        private void MoveSave(int delta)
        {
            if (delta < 0 && SaveList.SelectedIndex <= 0) { ExitSaveStatesToList(); return; }
            int n = SaveList.Items.Count;
            if (n == 0) return;
            int i = Math.Clamp(SaveList.SelectedIndex + delta, 0, n - 1);
            if (i != SaveList.SelectedIndex) SaveList.SelectedIndex = i;
        }

        // ── TV preview video ────────────────────────────────────────────────────
        private void OnGameSelectionChanged()
        {
            StopVideo();
            _videoDebounce.Stop();
            if (_mode == NavMode.GameList && GameList.SelectedItem is Game g)
            {
                _videoDebounce.Start();
                LoadSavesFor(g);
            }
        }

        private void OnVideoDebounceTick(object? sender, EventArgs e)
        {
            _videoDebounce.Stop();
            if (_closed || GameList.SelectedItem is not Game g) return;
            _ = TryPlayVideoForAsync(g);
        }

        private async Task TryPlayVideoForAsync(Game g)
        {
            int gen = _videoGen; // this request's generation; if it changes, we've moved on
            try
            {
                var ss = new ScreenScraperService();
                string? path = ss.FindCachedSnap(g.RomHash, g.Console);

                if (string.IsNullOrEmpty(path))
                {
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
                            if (gen == _videoGen) SetTvDownloading(false);
                        }
                    }
                }

                // Selection moved on while we were resolving/downloading → abandon.
                if (gen != _videoGen || _closed) return;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
                if (!ReferenceEquals(GameList.SelectedItem, g)) return;

                await PlayTvVideoAsync(path, g, gen);
            }
            catch { /* cosmetic */ }
        }

        private void SetTvDownloading(bool on) =>
            TvStandByImage.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        private async Task PlayTvVideoAsync(string mp4Path, Game forGame, int gen)
        {
            if (gen != _videoGen || _closed) return;

            _crossfadeDone = false;

            const int w = 320, h = 240;
            int stride = w * 4;

            if (_videoBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = Marshal.AllocHGlobal(stride * h);
            _videoBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            TvVideoImage.Source = _videoBitmap;

            // Per-play locals so each player only ever touches ITS OWN buffer/bitmap.
            IntPtr localBuffer = _videoBuffer;
            WriteableBitmap localBitmap = _videoBitmap;

            var libVLC = await VideoPlaybackService.Instance.GetLibVLCAsync();
            if (gen != _videoGen || _closed) return;

            await Task.Run(() =>
            {
                var player = new LibVLCSharp.Shared.MediaPlayer(libVLC);
                player.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)stride);

                player.SetVideoCallbacks(
                    (IntPtr opaque, IntPtr planes) => { Marshal.WriteIntPtr(planes, localBuffer); return IntPtr.Zero; },
                    null,
                    (IntPtr opaque, IntPtr picture) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            // Only blit if this play is still the current one AND its bitmap
                            // is still the live one — stops a stale player flickering in.
                            if (_closed || gen != _videoGen || !ReferenceEquals(_videoBitmap, localBitmap)) return;
                            localBitmap.Lock();
                            unsafe
                            {
                                Buffer.MemoryCopy(
                                    (void*)localBuffer, (void*)localBitmap.BackBuffer,
                                    (long)stride * h, (long)stride * h);
                            }
                            localBitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
                            localBitmap.Unlock();
                            if (!_crossfadeDone) { _crossfadeDone = true; TvVideoImage.Visibility = Visibility.Visible; }
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
                        if (_closed || gen != _videoGen || !ReferenceEquals(GameList.SelectedItem, forGame)) return;
                        // Never orphan a live player — dispose whatever's held before replacing.
                        if (_vlcPlayer != null)
                        {
                            try { _vlcPlayer.Stop(); } catch { }
                            try { _vlcPlayer.Dispose(); } catch { }
                        }
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
            _videoGen++; // cancel any in-flight fetch/play for the previous selection
            var p = _vlcPlayer;
            _vlcPlayer = null;
            if (p != null)
            {
                try { p.Stop(); } catch { }
                try { p.Dispose(); } catch { }
            }
            _crossfadeDone = false;
            TvVideoImage.Visibility = Visibility.Collapsed;
            TvStandByImage.Visibility = Visibility.Collapsed;
        }

        // ── Launch (reuses the desktop path; optional save-state to load) ─────────
        private void LaunchGame(Game game, string? statePath = null)
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

                StopVideo();
                _videoDebounce.Stop();
                _inputTimer?.Stop();

                string corePath = coreManager.GetCorePathForGame(game)!;
                EmulatorWindow.FreeStaleDll();
                var core = new LibretroCore(corePath);
                var emulator = new EmulatorWindow(game, core, statePath)
                    { Owner = this, StartInFullscreen = true };

                try { emulator.ShowDialog(); }
                finally
                {
                    _aLatch = true;
                    _bLatch = true;
                    _rightLatch = true;
                    _navDir = 0;
                    _navHoldTicks = 0;
                    _inputTimer?.Start();
                    // Refresh preview + saves (a new save may have been made in-game),
                    // regardless of which zone we launched from.
                    if (GameList.SelectedItem is Game refreshGame)
                    {
                        _videoDebounce.Stop();
                        _videoDebounce.Start();
                        LoadSavesFor(refreshGame);
                    }
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
                case Key.Left:
                    if (_mode == NavMode.Carousel)   { e.Handled = true; MoveCarousel(-1); }
                    else if (_mode == NavMode.SaveStates) { e.Handled = true; MoveSave(-1); }
                    break;
                case Key.Right:
                    if (_mode == NavMode.Carousel)   { e.Handled = true; MoveCarousel(1); }
                    else if (_mode == NavMode.GameList)   { e.Handled = true; EnterSaveStates(); }
                    else if (_mode == NavMode.SaveStates) { e.Handled = true; MoveSave(1); }
                    break;
                case Key.Up:    if (_mode == NavMode.GameList) { e.Handled = true; MoveGameList(-1); } break;
                case Key.Down:  if (_mode == NavMode.GameList) { e.Handled = true; MoveGameList(1);  } break;
                case Key.PageUp:   if (_mode == NavMode.GameList) { e.Handled = true; MovePageGameList(-1); } break;
                case Key.PageDown: if (_mode == NavMode.GameList) { e.Handled = true; MovePageGameList(1);  } break;
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

            // GameList: Right (edge) enters the save states.
            if (_mode == NavMode.GameList)
            {
                bool right = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_RIGHT)
                             || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_RIGHT);
                if (right && !_rightLatch) { _rightLatch = true; EnterSaveStates(); return; }
                if (!right) _rightLatch = false;
            }

            // GameList: L1/R1 page-jump through the library, accelerating on hold.
            if (_mode == NavMode.GameList)
            {
                bool r1 = _controller.IsRawXInputButtonDown(ControllerManager.RAW_RB); // page down
                bool l1 = _controller.IsRawXInputButtonDown(ControllerManager.RAW_LB); // page up
                int pdir = r1 ? 1 : l1 ? -1 : 0;
                if (pdir == 0) { _pageDir = 0; _pageHoldTicks = 0; }
                else if (pdir != _pageDir) { _pageDir = pdir; _pageHoldTicks = 0; MovePageGameList(pdir); }
                else
                {
                    _pageHoldTicks++;
                    if (_pageHoldTicks >= PageRepeatDelayTicks)
                    {
                        int held = _pageHoldTicks - PageRepeatDelayTicks;
                        int interval = Math.Max(PageRepeatMinTicks, PageRepeatStartTicks - held / PageRampEveryTicks);
                        if (held % interval == 0) MovePageGameList(pdir);
                    }
                }
            }

            // Directional nav with hold-to-repeat (axis depends on mode).
            int dir;
            if (_mode == NavMode.Carousel)
            {
                bool l = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_LEFT)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_LEFT);
                bool r = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_RIGHT)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_RIGHT);
                dir = r ? 1 : l ? -1 : 0;
            }
            else if (_mode == NavMode.GameList)
            {
                bool u = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_UP)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_UP);
                bool d = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_DOWN)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_DOWN);
                dir = d ? 1 : u ? -1 : 0;
            }
            else // SaveStates
            {
                bool l = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_LEFT)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_LEFT);
                bool r = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_RIGHT)
                         || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_RIGHT);
                dir = r ? 1 : l ? -1 : 0;
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
            switch (_mode)
            {
                case NavMode.Carousel:   MoveCarousel(dir); break;
                case NavMode.GameList:   MoveGameList(dir); break;
                case NavMode.SaveStates: MoveSave(dir); break;
            }
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

        private void MovePageGameList(int dir)
        {
            int n = GameList.Items.Count;
            if (n == 0) return;
            int page = GetGameListPageSize();
            int i = Math.Clamp(GameList.SelectedIndex + dir * page, 0, n - 1);
            if (i != GameList.SelectedIndex)
            {
                GameList.SelectedIndex = i;
                GameList.ScrollIntoView(GameList.Items[i]);
            }
        }

        // One "page" ≈ a screenful of rows (row ≈ 58 + 6 margin). Computed from the
        // list's height so it scales with resolution; clamped to a sane range.
        private int GetGameListPageSize()
        {
            int page = (int)(GameList.ActualHeight / 64.0);
            return Math.Clamp(page, 4, 40);
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _inputTimer?.Stop();
            _videoDebounce.Stop();
            StopVideo();

            _videoBitmap = null;
            var buf = _videoBuffer;
            _videoBuffer = IntPtr.Zero;
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);

            base.OnClosed(e);
        }
    }
}
