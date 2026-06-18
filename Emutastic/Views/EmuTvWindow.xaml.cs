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
using Emutastic.Models.EmuTv;
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
        private enum NavMode { Carousel, GameList, SaveStates, ThemeBrowser }

        private readonly ControllerManager? _controller;
        private readonly DatabaseService? _db;
        private readonly DispatcherTimer? _inputTimer;

        private NavMode _mode = NavMode.Carousel;
        private bool _bLatch;
        private bool _aLatch;
        private bool _yLatch;     // edge for opening the theme browser
        private bool _startLatch; // edge for opening/closing save states
        private bool _hotkeysLoaded;
        private ushort _hkThemeBrowser = ControllerManager.RAW_Y;   // EmuTV nav rebinds (Preferences -> EmuTV)
        private ushort _hkSaveStates   = ControllerManager.RAW_START;
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
        private Image? _videoTarget;   // themed <video> element to play into; null → TvVideoImage
        private DispatcherTimer? _imgReadyDebounce;   // coalesces re-renders as async webp decodes land
        private bool _crossfadeDone;
        private bool _closed;
        private int  _videoGen; // bumped on every stop/selection change to cancel in-flight video work

        public EmuTvWindow(ControllerManager? controller = null, DatabaseService? db = null)
        {
            InitializeComponent();
            _controller = controller;
            _db = db;

            // The active theme renders into ThemePreviewHost as the real UI; show it up-front so the
            // legacy hardcoded carousel/gamelist behind it (kept as the nav + data + video engine)
            // never flashes through. RenderActiveView() fills it once the library loads, and again on
            // every selection/mode change so navigation drives the themed UI.
            ThemePreviewHost.Visibility = Visibility.Visible;

            PreviewKeyDown += OnPreviewKeyDown;
            SystemCarousel.SelectionChanged += (_, _) => OnCarouselSelectionChanged();
            GameList.SelectionChanged += (_, _) => OnGameSelectionChanged();
            SaveList.SelectionChanged += (_, _) => { CenterSaveSelected(); RenderActiveView(); };
            ThemeBrowserList.SelectionChanged += (_, _) => UpdateThemeBrowserDetail();
            Loaded += (_, _) => LoadConsoles();
            UpdateHint();

            _videoDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _videoDebounce.Tick += OnVideoDebounceTick;

            _imgReadyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _imgReadyDebounce.Tick += (_, _) => { _imgReadyDebounce!.Stop(); if (!_closed) RenderActiveView(); };
            EmuTvThemeRenderer.OnAsyncImageReady = OnThemeImageReady;

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
            RenderActiveView();
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
            RenderActiveView();
        }

        private void EnterSaveStates()
        {
            if (_mode != NavMode.GameList) return;
            _mode = NavMode.SaveStates;
            // The "SAVE STATES" header is the VCR-cassette art (it carries the label). Load it once.
            if (SaveOverlayCassette.Source == null)
            {
                try
                {
                    string cassette = System.IO.Path.Combine(AppContext.BaseDirectory,
                        "Assets", "emutv-themes", "default", "assets", "save-states.png");
                    if (System.IO.File.Exists(cassette))
                        SaveOverlayCassette.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(cassette));
                }
                catch { /* fall back to no header art */ }
            }
            // Lift the save carousel out of the hidden legacy panel into the visible overlay so it
            // shows over whatever the active theme is drawing.
            if (SaveList.Parent is Panel home && !ReferenceEquals(home, SaveOverlayHost))
            {
                home.Children.Remove(SaveList);
                SaveOverlayHost.Children.Add(SaveList);
            }
            SaveOverlaySubtitle.Text = (GameList.SelectedItem as Game)?.Title ?? "";
            bool any = SaveList.Items.Count > 0;
            SaveOverlayEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (any && SaveList.SelectedIndex < 0) SaveList.SelectedIndex = 0;
            SaveOverlay.Visibility = Visibility.Visible;
            UpdateHint();
            _navDir = 0;
            _navHoldTicks = 0;
        }

        private void ExitSaveStatesToList()
        {
            _mode = NavMode.GameList;
            SaveOverlay.Visibility = Visibility.Collapsed;
            // Return the carousel to its home so it's ready to be reparented next time.
            if (ReferenceEquals(SaveList.Parent, SaveOverlayHost))
            {
                SaveOverlayHost.Children.Remove(SaveList);
                SaveListHome.Children.Add(SaveList);
            }
            UpdateHint();
            _navDir = 0;
            _navHoldTicks = 0;
        }

        private void UpdateHint() =>
            HintLabel.Text = _mode switch
            {
                NavMode.Carousel   => "◀ ▶  Navigate      A  Open      B  Back",
                NavMode.GameList   => "▲ ▼  Games      Start  Save states      A  Play      B  Back",
                _                  => "◀ ▶  Save states      A  Load      ◀  Back to games",
            };

        private void OnAccept()
        {
            if (_mode == NavMode.ThemeBrowser) { AcceptThemeBrowser(); return; }
            if (_mode == NavMode.Carousel) OpenSelectedConsole();
            else if (_mode == NavMode.GameList && GameList.SelectedItem is Game g) LaunchGame(g);
            else if (_mode == NavMode.SaveStates
                     && GameList.SelectedItem is Game game
                     && SaveList.SelectedItem is SaveState s)
            {
                ExitSaveStatesToList();        // close the overlay before the emulator takes over
                LaunchGame(game, s.StatePath);
            }
        }

        private void OnBack()
        {
            switch (_mode)
            {
                case NavMode.ThemeBrowser: CloseThemeBrowser(); break;
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
            RenderActiveView();
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

            // Per-play locals so each player only ever touches ITS OWN buffer/bitmap/sink. The sink is
            // the themed <video> element if one rendered, otherwise the default TV image.
            IntPtr localBuffer = _videoBuffer;
            WriteableBitmap localBitmap = _videoBitmap;
            Image sink = _videoTarget ?? TvVideoImage;

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
                            if (!_crossfadeDone) { _crossfadeDone = true; sink.Source = localBitmap; sink.Visibility = Visibility.Visible; }
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
                    else if (_mode == NavMode.ThemeBrowser) { e.Handled = true; CycleBrowserColor(-1); }
                    break;
                case Key.Right:
                    if (_mode == NavMode.Carousel)   { e.Handled = true; MoveCarousel(1); }
                    else if (_mode == NavMode.GameList)   { e.Handled = true; EnterSaveStates(); }
                    else if (_mode == NavMode.SaveStates) { e.Handled = true; MoveSave(1); }
                    else if (_mode == NavMode.ThemeBrowser) { e.Handled = true; CycleBrowserColor(1); }
                    break;
                case Key.Up:    if (_mode == NavMode.GameList) { e.Handled = true; MoveGameList(-1); }
                                else if (_mode == NavMode.ThemeBrowser) { e.Handled = true; MoveThemeBrowser(-1); } break;
                case Key.Down:  if (_mode == NavMode.GameList) { e.Handled = true; MoveGameList(1);  }
                                else if (_mode == NavMode.ThemeBrowser) { e.Handled = true; MoveThemeBrowser(1); } break;
                case Key.OemOpenBrackets:  if (_mode == NavMode.ThemeBrowser) { e.Handled = true; CycleBrowserVariant(-1); } break;
                case Key.OemCloseBrackets: if (_mode == NavMode.ThemeBrowser) { e.Handled = true; CycleBrowserVariant(1);  } break;
                case Key.PageUp:   if (_mode == NavMode.GameList) { e.Handled = true; MovePageGameList(-1); } break;
                case Key.PageDown: if (_mode == NavMode.GameList) { e.Handled = true; MovePageGameList(1);  } break;
                case Key.T:        e.Handled = true; OpenThemeBrowser(); break;
                case Key.F6:       e.Handled = true; ImportThemeViaDialog(); break;
                case Key.F7:       e.Handled = true; CycleActiveTheme(); break;
                case Key.F8:       e.Handled = true;   // debug: peek at the legacy UI behind the theme
                    ThemePreviewHost.Visibility = ThemePreviewHost.Visibility == Visibility.Visible
                        ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case Key.F9:       e.Handled = true; SnapshotThemeView(); break;
            }
        }

        // EmuTV nav hotkeys are rebindable in Preferences -> EmuTV (stored in EmuTvConfiguration).
        private void LoadEmuTvHotkeys()
        {
            var cfg = App.Configuration?.GetEmuTvConfiguration();
            if (cfg == null) return;
            if (cfg.HotkeyOverrides.TryGetValue("theme_browser", out var tb)) _hkThemeBrowser = ButtonNameToRaw(tb, ControllerManager.RAW_Y);
            if (cfg.HotkeyOverrides.TryGetValue("save_states", out var ss))   _hkSaveStates   = ButtonNameToRaw(ss, ControllerManager.RAW_START);
        }

        private static ushort ButtonNameToRaw(string? name, ushort dflt) => name switch
        {
            "A" => ControllerManager.RAW_A, "B" => ControllerManager.RAW_B,
            "X" => ControllerManager.RAW_X, "Y" => ControllerManager.RAW_Y,
            "Back" => ControllerManager.RAW_BACK, "Start" => ControllerManager.RAW_START,
            "L1" => ControllerManager.RAW_LB, "R1" => ControllerManager.RAW_RB,
            _ => dflt,
        };

        private void OnInputTick(object? sender, EventArgs e)
        {
            if (_controller == null) return;
            if (!_hotkeysLoaded) { LoadEmuTvHotkeys(); _hotkeysLoaded = true; }

            bool b = _controller.IsRawXInputButtonDown(ControllerManager.RAW_B);
            if (b && !_bLatch) { _bLatch = true; OnBack(); return; }
            if (!b) _bLatch = false;

            bool a = _controller.IsRawXInputButtonDown(ControllerManager.RAW_A);
            if (a && !_aLatch) { _aLatch = true; OnAccept(); return; }
            if (!a) _aLatch = false;

            bool y = _controller.IsRawXInputButtonDown(_hkThemeBrowser);
            if (y && !_yLatch) { _yLatch = true; OpenThemeBrowser(); return; }
            if (!y) _yLatch = false;

            bool start = _controller.IsRawXInputButtonDown(_hkSaveStates);
            if (start && !_startLatch)
            {
                _startLatch = true;
                if (_mode == NavMode.GameList) EnterSaveStates();
                else if (_mode == NavMode.SaveStates) ExitSaveStatesToList();
                return;
            }
            if (!start) _startLatch = false;

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

            // ThemeBrowser: ◀▶ cycle colour scheme, L1/R1 cycle variant (both edge-triggered so one
            // press = one step). Up/down still drives the theme list via the directional block below.
            if (_mode == NavMode.ThemeBrowser)
            {
                bool axl = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_LEFT)
                           || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_LEFT);
                bool axr = _controller.IsRawXInputButtonDown(ControllerManager.RAW_DPAD_RIGHT)
                           || _controller.GetButtonState(ControllerManager.ANALOG_LEFT_RIGHT);
                int cdir = axr ? 1 : axl ? -1 : 0;
                if (cdir != 0) { if (!_axisColorLatch) { _axisColorLatch = true; CycleBrowserColor(cdir); } }
                else _axisColorLatch = false;

                bool vl = _controller.IsRawXInputButtonDown(ControllerManager.RAW_LB);
                bool vr = _controller.IsRawXInputButtonDown(ControllerManager.RAW_RB);
                int vdir = vr ? 1 : vl ? -1 : 0;
                if (vdir != 0) { if (!_axisVariantLatch) { _axisVariantLatch = true; CycleBrowserVariant(vdir); } }
                else _axisVariantLatch = false;
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
            else if (_mode == NavMode.GameList || _mode == NavMode.ThemeBrowser)
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
                case NavMode.Carousel:    MoveCarousel(dir); break;
                case NavMode.GameList:    MoveGameList(dir); break;
                case NavMode.SaveStates:  MoveSave(dir); break;
                case NavMode.ThemeBrowser: MoveThemeBrowser(dir); break;
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

        // ── Theme preview overlay (F8) — non-destructive renderer validation ──────
        // Renders the active EmuTV theme's current view on top of the live shell so the
        // renderer's layout/colors/assets can be eyeballed before the full host refactor.
        private RenderedView? _themePreview;
        private EmuTvThemeParseResult? _activeThemeRes;
        private string? _activeThemeId;

        // Renders the active theme's current view (system/gamelist) into the window. Re-run on
        // every navigation/selection/mode change so the themed UI tracks input. The active theme is
        // parsed once and cached; image loads are cached too, so re-renders are cheap.
        private void RenderActiveView()
        {
            try
            {
                string id = EmuTvThemeService.Instance.ActiveThemeId;
                if (_activeThemeRes == null || _activeThemeId != id)
                {
                    _activeThemeRes = EmuTvThemeService.Instance.LoadActiveTheme();
                    _activeThemeId = id;
                }
                var variant = _activeThemeRes?.Theme.Variants.Values.FirstOrDefault();
                if (variant == null) return;

                var kind = _mode == NavMode.Carousel ? ThemeViewKind.System : ThemeViewKind.Gamelist;
                var view = variant.Views.FirstOrDefault(v => v.Kind == kind)
                           ?? variant.Views.FirstOrDefault();
                if (view == null) return;

                double w = ActualWidth > 0 ? ActualWidth : 1920;
                double h = ActualHeight > 0 ? ActualHeight : 1080;
                // The faithful engine binds all data (system logos, game art, metadata text, help)
                // directly from the snapshot during render — no post-pass slot filling needed.
                _themePreview = new EmuTvThemeRenderer(_activeThemeRes!.Theme.RootPath).Render(view, w, h,
                    EsSystemName.For((SystemCarousel.SelectedItem as ConsoleGroup)?.ConsoleName),
                    BuildThemeItems());

                ThemePreviewHost.Content = _themePreview.Root;
                ThemePreviewHost.Visibility = Visibility.Visible;
                _videoTarget = _themePreview.VideoTarget;   // live video plays into the themed <video>
                // If a video is already playing, re-point it at the freshly-rendered target. Without this,
                // an async re-render (a webp decode landing, a save-list change) strands the live video in
                // the old, now-detached target Image while the new one shows only its poster — which is why
                // webp/async themes appeared to "lose" video. The player keeps blitting the same bitmap, so
                // pointing the new target at it resumes playback in place.
                if (_videoTarget != null && _crossfadeDone && _videoBitmap != null)
                {
                    _videoTarget.Source = _videoBitmap;
                    _videoTarget.Visibility = Visibility.Visible;
                }
            }
            catch { /* render is best-effort */ }
        }

        // Fired off-thread when a webp finishes decoding — coalesce into a single re-render so the
        // newly-available images appear without blocking or thrashing.
        private void OnThemeImageReady()
        {
            try { Dispatcher.BeginInvoke(() => { if (!_closed) { _imgReadyDebounce?.Stop(); _imgReadyDebounce?.Start(); } }); }
            catch { }
        }

        // F9 — capture exactly what the themed view is rendering to a PNG, so rendering issues can be
        // diagnosed by looking instead of guessing.
        private void SnapshotThemeView()
        {
            try
            {
                int w = (int)ThemePreviewHost.ActualWidth, h = (int)ThemePreviewHost.ActualHeight;
                if (w <= 0 || h <= 0) return;
                var rtb = new RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(ThemePreviewHost);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string path = @"D:\Emutastic Data\Logs\emutv-snapshot.png";
                using var fs = System.IO.File.Create(path);
                enc.Save(fs);
                HintLabel.Text = "Snapshot saved";
            }
            catch { }
        }

        // F7 — cycle the active EmuTV theme (so an imported theme can be previewed). If the
        // preview is open, re-render it with the newly active theme.
        private void CycleActiveTheme()
        {
            var themes = EmuTvThemeService.Instance.AvailableThemes;
            if (themes.Count == 0) return;
            string cur = EmuTvThemeService.Instance.ActiveThemeId;
            int idx = -1;
            for (int i = 0; i < themes.Count; i++) if (themes[i].Id == cur) { idx = i; break; }
            var next = themes[(idx + 1) % themes.Count];
            EmuTvThemeService.Instance.SetActiveTheme(next.Id);
            _activeThemeRes = null;            // force a re-parse of the newly active theme
            ThemePreviewHost.Visibility = Visibility.Visible;
            RenderActiveView();
        }

        // ── Unified theme browser (installed + downloadable catalog) ──────────────
        private sealed class BrowserEntry
        {
            public string Name { get; init; } = "";
            public string Author { get; set; } = "";
            public string AuthorLine => string.IsNullOrWhiteSpace(Author) ? "" : "by " + Author;
            public string StatusText { get; init; } = "";
            public Brush StatusBrush { get; init; } = Brushes.Gray;
            public bool IsInstalled { get; init; }
            public string? ThemeId { get; init; }        // installed id (for apply)
            public CatalogTheme? Catalog { get; init; }  // catalog source (for download)
            public string? PreviewUrl { get; set; }      // enriched from the catalog after fetch
            public string Meta { get; set; } = "";
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<BrowserEntry> _browserEntries = new();
        private NavMode _modeBeforeBrowser = NavMode.Carousel;
        private bool _browserBusy;

        // Axis picker — colour scheme + variant for the currently-selected installed browser entry.
        private List<ColorSchemeDef> _axisColors = new();
        private List<VariantDef> _axisVariants = new();
        private int _axisColorIdx, _axisVariantIdx;
        private bool _axisColorLatch, _axisVariantLatch;

        private void OpenThemeBrowser()
        {
            if (_mode == NavMode.ThemeBrowser) return;
            _modeBeforeBrowser = _mode == NavMode.SaveStates ? NavMode.GameList : _mode;
            _mode = NavMode.ThemeBrowser;
            ThemeBrowserList.ItemsSource = _browserEntries;
            ThemeBrowser.Visibility = Visibility.Visible;
            _ = PopulateThemeBrowserAsync();
        }

        private async Task PopulateThemeBrowserAsync()
        {
            static string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

            _browserEntries.Clear();
            var installed = EmuTvThemeService.Instance.AvailableThemes;
            var installedByNorm = new HashSet<string>(installed.Select(i => Norm(i.Name)));

            var instBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0xE0, 0x9A));
            var dlBrush   = new SolidColorBrush(Color.FromRgb(0x6F, 0xC8, 0xFF));

            // Installed first (shows instantly; previews/meta are enriched from the catalog below).
            var instEntries = new List<BrowserEntry>();
            foreach (var i in installed)
            {
                var en = new BrowserEntry
                {
                    Name = i.Name, Author = i.Author, IsInstalled = true, ThemeId = i.Id,
                    StatusText = "INSTALLED", StatusBrush = instBrush,
                };
                instEntries.Add(en);
                _browserEntries.Add(en);
            }
            if (_browserEntries.Count > 0) ThemeBrowserList.SelectedIndex = 0;
            UpdateThemeBrowserDetail();

            // Catalog: match installed themes to their catalog entry (by normalized name) to borrow a
            // preview screenshot + metadata, then append the not-yet-installed ones as downloads.
            var catalog = await EmuTvThemeCatalog.Instance.FetchAsync();
            var byNorm = new Dictionary<string, CatalogTheme>();
            foreach (var c in catalog) byNorm.TryAdd(Norm(c.Name), c);

            foreach (var en in instEntries)
                if (byNorm.TryGetValue(Norm(en.Name), out var m))
                {
                    en.PreviewUrl = EmuTvThemeCatalog.Instance.ScreenshotUrl(m);
                    if (string.IsNullOrWhiteSpace(en.Author)) en.Author = m.Author;
                    en.Meta = BuildCatalogMeta(m);
                }

            foreach (var c in catalog)
            {
                if (installedByNorm.Contains(Norm(c.Name))) continue;
                _browserEntries.Add(new BrowserEntry
                {
                    Name = c.Name, Author = c.Author, IsInstalled = false, Catalog = c,
                    StatusText = "DOWNLOAD", StatusBrush = dlBrush,
                    PreviewUrl = EmuTvThemeCatalog.Instance.ScreenshotUrl(c),
                    Meta = BuildCatalogMeta(c),
                });
            }
            if (ThemeBrowserList.SelectedIndex < 0 && _browserEntries.Count > 0)
                ThemeBrowserList.SelectedIndex = 0;
            UpdateThemeBrowserDetail();   // refresh the selected entry now that previews are enriched
        }

        private static string BuildCatalogMeta(CatalogTheme c)
        {
            var parts = new List<string>();
            if (c.Variants.Count > 0) parts.Add($"{c.Variants.Count} variant{(c.Variants.Count == 1 ? "" : "s")}");
            if (c.ColorSchemes.Count > 0) parts.Add($"{c.ColorSchemes.Count} color scheme{(c.ColorSchemes.Count == 1 ? "" : "s")}");
            if (c.AspectRatios.Count > 0) parts.Add($"{c.AspectRatios.Count} aspect ratios");
            return string.Join("    ·    ", parts);
        }

        private readonly Dictionary<string, ImageSource?> _previewCache = new();

        private async void UpdateThemeBrowserDetail()
        {
            if (ThemeBrowserList.SelectedItem is not BrowserEntry e)
            {
                ThemeBrowserName.Text = ""; ThemeBrowserMeta.Text = ""; ThemeBrowserPreview.Source = null;
                ThemeBrowserAxes.Visibility = Visibility.Collapsed;
                return;
            }
            ThemeBrowserName.Text = e.Name;
            ThemeBrowserMeta.Text = string.IsNullOrEmpty(e.Meta)
                ? e.AuthorLine
                : (string.IsNullOrEmpty(e.AuthorLine) ? e.Meta : e.AuthorLine + "\n" + e.Meta);
            RefreshAxisOptions(e);

            ThemeBrowserPreview.Source = null;
            string? url = e.PreviewUrl;
            if (string.IsNullOrEmpty(url)) return;

            if (_previewCache.TryGetValue(url, out var cached)) { ThemeBrowserPreview.Source = cached; return; }

            // Download via the pooled HttpClient (not BitmapImage's capped URL loader), decode from
            // memory, cache, and only apply if the user is still on this entry.
            byte[]? bytes = await EmuTvThemeCatalog.Instance.GetBytesAsync(url);
            ImageSource? img = null;
            if (bytes != null)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new System.IO.MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
                catch { /* undecodable preview */ }
            }
            _previewCache[url] = img;
            if (ReferenceEquals(ThemeBrowserList.SelectedItem, e))
                ThemeBrowserPreview.Source = img;
        }

        private void MoveThemeBrowser(int delta)
        {
            int n = ThemeBrowserList.Items.Count;
            if (n == 0) return;
            int i = Math.Clamp(ThemeBrowserList.SelectedIndex + delta, 0, n - 1);
            if (i != ThemeBrowserList.SelectedIndex)
            {
                ThemeBrowserList.SelectedIndex = i;
                ThemeBrowserList.ScrollIntoView(ThemeBrowserList.Items[i]);
            }
        }

        // ── axis picker: cycle colour scheme + variant for the selected installed theme ───────────
        // The pick is shown in the detail pane and committed (persisted + re-parsed) on Apply.
        private void RefreshAxisOptions(BrowserEntry e)
        {
            var caps = (e.IsInstalled && e.ThemeId != null)
                ? EmuTvThemeService.Instance.GetCapabilities(e.ThemeId) : null;
            _axisColors   = caps?.ColorSchemes ?? new List<ColorSchemeDef>();
            _axisVariants = caps?.Variants?.Where(v => v.Selectable).ToList() ?? new List<VariantDef>();
            // Seed each axis from the persisted pick (when this theme has it), else the theme's default (0).
            var sel = EmuTvThemeService.Instance.GetSelection();
            _axisColorIdx   = Math.Max(0, _axisColors.FindIndex(c => c.Name == sel.ColorScheme));
            _axisVariantIdx = Math.Max(0, _axisVariants.FindIndex(v => v.Name == sel.Variant));
            if (_axisColors.Count <= 1 && _axisVariants.Count <= 1)
            {
                ThemeBrowserAxes.Visibility = Visibility.Collapsed;
                return;
            }
            ThemeBrowserAxes.Visibility = Visibility.Visible;
            UpdateAxisLabels();
        }

        private void UpdateAxisLabels()
        {
            bool hasColors = _axisColors.Count > 1, hasVariants = _axisVariants.Count > 1;
            ThemeBrowserColors.Visibility  = hasColors  ? Visibility.Visible : Visibility.Collapsed;
            ThemeBrowserVariant.Visibility = hasVariants ? Visibility.Visible : Visibility.Collapsed;
            if (hasColors)
                ThemeBrowserColors.Text =
                    $"◀  Color:  {_axisColors[_axisColorIdx].Label}  ▶    ({_axisColorIdx + 1}/{_axisColors.Count})";
            if (hasVariants)
                ThemeBrowserVariant.Text = $"L1 R1  Variant:  {_axisVariants[_axisVariantIdx].Label}";
        }

        private void CycleBrowserColor(int dir)
        {
            int n = _axisColors.Count;
            if (n < 2) return;
            _axisColorIdx = ((_axisColorIdx + dir) % n + n) % n;
            UpdateAxisLabels();
        }

        private void CycleBrowserVariant(int dir)
        {
            int n = _axisVariants.Count;
            if (n < 2) return;
            _axisVariantIdx = ((_axisVariantIdx + dir) % n + n) % n;
            UpdateAxisLabels();
        }

        // Persist the picked colour scheme + variant so the next parse (on Apply) uses them.
        private void CommitAxisSelection()
        {
            if (_axisColors.Count == 0 && _axisVariants.Count == 0) return;
            var sel = EmuTvThemeService.Instance.GetSelection();
            if (_axisColors.Count   > 0) sel.ColorScheme = _axisColors[_axisColorIdx].Name;
            if (_axisVariants.Count > 0) sel.Variant     = _axisVariants[_axisVariantIdx].Name;
            EmuTvThemeService.Instance.SetSelection(sel);
        }

        private async void AcceptThemeBrowser()
        {
            if (_browserBusy || ThemeBrowserList.SelectedItem is not BrowserEntry e) return;

            if (e.IsInstalled && e.ThemeId != null) { CommitAxisSelection(); ApplyThemeAndClose(e.ThemeId); return; }
            if (e.Catalog == null) return;

            _browserBusy = true;
            ThemeBrowserHint.Text = $"Downloading “{e.Name}”…  please wait";
            EsDeImportResult result;
            try { result = await EmuTvThemeCatalog.Instance.DownloadAndInstallAsync(e.Catalog); }
            finally { _browserBusy = false; }

            if (result.Ok && result.Id != null)
                ApplyThemeAndClose(result.Id);
            else
                ThemeBrowserHint.Text = "Download failed: " + result.Message + "        B  Close";
        }

        private void ApplyThemeAndClose(string themeId)
        {
            EmuTvThemeService.Instance.SetActiveTheme(themeId);
            _activeThemeRes = null;
            CloseThemeBrowser();
            ThemePreviewHost.Visibility = Visibility.Visible;
            RenderActiveView();
        }

        private void CloseThemeBrowser()
        {
            ThemeBrowser.Visibility = Visibility.Collapsed;
            ThemeBrowserHint.Text = "▲ ▼  Browse      ◀ ▶  Color      L1 R1  Variant      A  Apply / Download      B  Close";
            _mode = _modeBeforeBrowser;
        }

        // Snapshots the real library (consoles + the selected console's games) so the renderer can
        // draw the theme's primary-nav items — system logos, game box art — faithfully.
        private ThemeItemData BuildThemeItems()
        {
            var systems = new List<ThemeSystemEntry>();
            if (SystemCarousel.ItemsSource is System.Collections.IEnumerable src)
                foreach (ConsoleGroup c in src)
                    systems.Add(new ThemeSystemEntry
                    {
                        Label = c.ConsoleName,
                        EsName = EsSystemName.For(c.ConsoleName),
                        IconPath = ConsoleItem.ResolveIconUri(c.ConsoleName),
                    });

            var games = new List<ThemeGameEntry>();
            var selConsole = SystemCarousel.SelectedItem as ConsoleGroup;
            // No cap — the renderer windows the list (only a screenful of items is ever
            // materialized, see BuildGrid/BuildTextList), so the full library navigates
            // smoothly regardless of size. An earlier .Take(200) silently truncated large
            // libraries (e.g. scrolling halted partway through a 700+ game console).
            if (selConsole?.Games != null)
                foreach (var g in selConsole.Games)
                    games.Add(new ThemeGameEntry
                    {
                        Label = g.Title,
                        CoverPath = g.CoverArtPath,
                        ScreenshotPath = g.ScreenScraperArtPath,
                        Box3dPath = g.BoxArt3DPath,
                        MarqueePath = g.CoverArtPath,
                        Developer = g.Developer,
                        Publisher = g.Publisher,
                        Genre = g.Genre,
                        Description = g.Description,
                        Year = g.Year,
                        RatingStars = g.RatingStars,
                        Rating = Math.Clamp(g.Rating / 5.0, 0, 1),
                        Favorite = g.IsFavorite,
                    });

            return new ThemeItemData
            {
                Systems = systems,
                SelectedSystem = Math.Max(0, SystemCarousel.SelectedIndex),
                Games = games,
                SelectedGame = Math.Max(0, GameList.SelectedIndex),
                SystemName = selConsole?.ConsoleName ?? "",
                SystemGameCount = selConsole != null ? $"{selConsole.TotalCount} games" : "",
                HelpText = HintLabel.Text,
            };
        }

        // F6 — import an ES-DE theme folder, make it active, and preview it.
        private void ImportThemeViaDialog()
        {
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select an ES-DE theme folder to import into EmuTV",
                    UseDescriptionForTitle = true,
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                var result = EsDeThemeImporter.ImportFromFolder(dlg.SelectedPath);
                if (result.Ok && result.Id != null)
                {
                    EmuTvThemeService.Instance.SetActiveTheme(result.Id);
                    _activeThemeRes = null;            // force a re-parse of the imported theme
                    ThemePreviewHost.Visibility = Visibility.Visible;
                    RenderActiveView();
                }
                else
                {
                    MessageBox.Show(this, result.Message, "Theme import",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Theme import failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            EmuTvThemeRenderer.OnAsyncImageReady = null;
            _imgReadyDebounce?.Stop();
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
