using Microsoft.Win32;
using Emutastic.Models;
using Emutastic.Services;
using Emutastic.ViewModels;
using Emutastic.Views;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Emutastic
{
    public partial class MainWindow : Window
    {
        private MainViewModel _vm = null!;
        private DatabaseService _db = null!;
        private ImportService _importer = null!;
        private ArtworkService _artwork = null!;
        private ArtworkFetchService _artworkFetch = null!;
        private ControllerManager? _controllerManager;
        private CoreManager _coreManager = null!;
        private Button? _selectedNavButton;
        private Game?   _selectionAnchor;   // anchor for Shift+click range selection
        private readonly HashSet<string> _selectedScreenshots = new(); // selected file paths
        private System.Windows.Threading.DispatcherTimer? _dragLeaveTimer;
        private GameDetailWindow? _openDetailWindow;
        // _vm.IsShowingFavorites moved to MainViewModel
        private string _currentNavTag = "All Games";
        private readonly Dictionary<string, double> _scrollPositions = new();

        public MainWindow()
        {
            InitializeComponent();
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/emutastic-logo.ico"));
            ApplyWindowsChrome();
            AllowDrop = true;

            // GridView column-header clicks bubble up the visual tree as a
            // routed Click event. ListView doesn't surface them as a normal
            // event, so wire it manually with AddHandler.
            GameListView.AddHandler(GridViewColumnHeader.ClickEvent,
                new RoutedEventHandler(GameListColumnHeader_Click));

            // Everything else deferred to Loaded so the window appears immediately.
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded; // fire once

            // ── Phase 1: synchronous, fast — window becomes interactive immediately ──
            _db          = new DatabaseService();   // schema init (CREATE TABLE / indexes)

            // Warm every Preferences-tab cache in the background so opening
            // Preferences and clicking any tab is instant — no "Loading…" ever.
            // Fire-and-forget; failures fall back to the per-tab builders.
            try
            {
                Services.PreferencesCache.WarmUp(
                    _db,
                    AppPaths.GetFolder("System"),
                    AppPaths.GetCoresFolder());
            }
            catch { }

            // Pre-initialize SDL3 on the dispatcher at Background priority so
            // the controller-name enumeration is warm by the time the user
            // opens Preferences. Must be the dispatcher (SDL hooks
            // WM_DEVICECHANGE on the calling thread). Background priority
            // means the main window paints + becomes interactive first — the
            // (occasionally many-second) SDL_Init cost runs during dispatcher
            // idle, not as a visible freeze. If the user opens Preferences
            // before this completes, GetConnectedControllers falls through to
            // the XInput path for an instant result with generic names; the
            // 2-second hot-plug timer in Preferences upgrades to SDL names
            // once init finishes.
            Services.ControllerManager.EnsureSdl3InitInBackground();
            // Live-refresh the Save States tab when a state is added/deleted/renamed
            // anywhere (EmulatorWindow during play, context-menu rename/delete,
            // startup orphan-discovery). Static event so events fired by
            // EmulatorWindow's separate DatabaseService instance still reach us.
            // Marshal to UI; only repopulate when the tab is currently visible
            // to avoid wasted work.
            DatabaseService.SaveStatesChanged += (_, _) =>
            {
                System.Diagnostics.Trace.WriteLine("[SaveStatesView] event received, marshaling to UI");
                Dispatcher.BeginInvoke(() =>
                {
                    bool visible = SaveStatesView != null && SaveStatesView.Visibility == Visibility.Visible;
                    System.Diagnostics.Trace.WriteLine($"[SaveStatesView] UI handler — visible={visible}");
                    if (visible) PopulateSaveStatesView();
                });
            };
            _artwork     = new ArtworkService();
            _coreManager = new CoreManager(App.Configuration!);
            _importer    = new ImportService(_db, _coreManager, App.Configuration);
            _vm          = new MainViewModel(_db);  // empty _allGames until Reload() runs
            _artworkFetch = new ArtworkFetchService(_db, _artwork, _vm);
            _vm.Navigated += OnNavigated;
            _artworkFetch.BoxArt3DFetched += () =>
                Dispatcher.Invoke(() => BoxArtTogglePanel.Visibility = Visibility.Visible);
            DataContext  = _vm;                     // _vm is now non-null; clicks work

            _importer.StatusChanged += msg =>
                Dispatcher.Invoke(() =>
                {
                    SetStatus(msg);
                    // Source `IsImporting` from the service's authoritative flag —
                    // late artwork-task StatusChanged events fire AFTER drain sets
                    // it false, and we don't want them re-animating the banner.
                    _vm.IsImporting = _importer.IsImporting;
                    _vm.ImportStatusText = msg;
                });

            _importer.ProgressChanged += (current, total) =>
                Dispatcher.Invoke(() =>
                {
                    if (total == 0) return;
                    _vm.IsImporting = _importer.IsImporting;
                    if (current >= total)
                    {
                        SetStatus("Import complete", autoClear: true);
                        _vm.ImportProgressPercent = 100;
                        return;
                    }
                    int pct = (int)((current / (double)total) * 100);
                    string headline = $"Importing… {pct}%  ({current} of {total})";
                    SetStatus(headline);
                    _vm.ImportStatusText = headline;
                    _vm.ImportProgressPercent = pct;
                });
            _importer.GameImported += game =>
                Dispatcher.Invoke(() =>
                {
                    _vm.RefreshGame(game);
                    UpdateBoxArtToggleVisibility();
                });
            _importer.ImportQueueDrained += () =>
                Dispatcher.Invoke(async () =>
                {
                    await Task.Run(() => _vm.Reload());
                    await _vm.FilterGamesAsync();
                    _vm.ToolbarTitle = _vm.SelectedConsole;
                    _vm.IsImporting = false;
                    _vm.ImportStatusText = "";
                    _vm.ImportProgressPercent = 0;
                });
            _importer.AmbiguousConsoleResolver = (fileName, candidates) =>
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
                Dispatcher.Invoke(() =>
                {
                    var picker = new Views.ConsolePickerWindow(fileName, candidates) { Owner = this };
                    tcs.SetResult(picker.ShowDialog() == true ? picker.SelectedConsole : null);
                });
                return tcs.Task;
            };

            RestoreMainWindowBounds();
            Closing += MainWindow_Closing;

            UpdateTabStyles("Library");
            RefreshCollectionsSidebar();

            // Restore per-console 3D box art preferences BEFORE loading games,
            // so DisplayArtPath evaluates correctly during initial binding.
            var snapCfg = App.Configuration?.GetSnapConfiguration();
            if (snapCfg?.Use3DBoxArtConsoles?.Count > 0)
                Game.Consoles3D = new System.Collections.Generic.HashSet<string>(snapCfg.Use3DBoxArtConsoles);
            if (snapCfg?.ScreenScraperMaxThreads > 0)
                ScreenScraperService.SetMaxThreads(snapCfg.ScreenScraperMaxThreads);
            Game.PreferScreenScraper2D = snapCfg?.PreferScreenScraper2D == true;

            // ── Phase 2: load data off UI thread, then filter on UI thread ──
            await Task.Run(() => _vm.Reload());  // GetAllGames() — stays off UI thread
            await _vm.FilterGamesAsync();        // sort/group in background, assign on UI thread
            ScrollLibraryToTop();

            UpdateBoxArtToggleVisibility();

            // Pre-build per-console caches in the background so switching feels instant.
            _ = _vm.PreloadConsoleCachesAsync();

            // Controller hot-plug status — surface connect/disconnect events
            // in the existing status text for 15s so the user sees their pad
            // come up without having to open Preferences. SDL3 is initializing
            // on its own thread in the background; the first ticks may use the
            // XInput fallback (generic names) until SDL3 is ready, then
            // upgrade to full names.
            StartControllerStatusPoll();

            _ = _artworkFetch.RetryMissingArtworkAsync();
            _ = _artworkFetch.BackfillMetadataAsync();

            // Auto-resume any in-progress arcade/neogeo metadata refresh that
            // didn't finish in a previous session (e.g. user closed the app
            // mid-pass, or cancelled via the banner click). The same banner +
            // click-to-stop UX applies. No-op when fully populated.
            AutoResumeArcadeMetadataRefresh();

            // Discover save states on disk that aren't in the database.
            // Quick check — only scans if the DB has fewer states than what's on disk.
            _ = Task.Run(() =>
            {
                int found = _db.DiscoverOrphanedSaveStates();
                if (found > 0)
                    Dispatcher.BeginInvoke(() =>
                        SetStatus($"Discovered {found} save state(s)", autoClear: true));
            });

            ApplyBackgroundImage();

            // Background scan for stale libretro cores. Fire-and-forget — never
            // blocks startup, falls silent if the user is offline (CheckAsync
            // swallows network errors). Result surfaces in the bottom-left
            // banner as a non-blocking nudge that auto-hides after 20 seconds.
            _ = CheckCoreUpdatesAndNotifyAsync();
        }

        private async Task CheckCoreUpdatesAndNotifyAsync()
        {
            try
            {
                string coresFolder = AppPaths.GetCoresFolder();
                var updates = await new Services.CoreDownloadService()
                    .CheckAllForUpdatesAsync(coresFolder);
                if (updates.Count == 0) return;

                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.Invoke(() =>
                {
                    _vm.NotificationText = updates.Count == 1
                        ? "1 core update available — Preferences → Cores"
                        : $"{updates.Count} core updates available — Preferences → Cores";
                    _vm.IsNotification = true;
                });

                await Task.Delay(20_000);

                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.Invoke(() =>
                {
                    _vm.IsNotification = false;
                    _vm.NotificationText = "";
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[CoreUpdateCheck] {ex.Message}");
            }
        }

        private void BannerBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Banner is clickable only when surfacing a notification (not during
            // an import). Two click behaviors depending on what the banner is
            // currently showing:
            //   - A metadata refresh in progress → click STOPS the refresh. State
            //     is naturally resumable (filter skips already-filled games on
            //     the next Refresh Library click), so users can come back later.
            //   - Anything else (core-updates notification) → open Preferences →
            //     Cores so they can act on it.
            if (!_vm.IsNotification) return;

            if (_metadataRefreshCts != null && !_metadataRefreshCts.IsCancellationRequested)
            {
                _metadataRefreshCts.Cancel();
                return;
            }

            _vm.IsNotification = false;
            _vm.NotificationText = "";
            InitializeControllerManager();
            var prefs = new Views.PreferencesWindow(_db, _controllerManager!, App.Configuration!) { Owner = this };
            prefs.OpenSection("Cores");
            prefs.ShowDialog();
        }

        // Cancellation source for the currently-running metadata refresh, if any.
        // Null when no refresh is in flight; cancelled when the user clicks the
        // banner during a refresh. Tracked here so any caller (Refresh Library,
        // startup auto-resume) can share the same cancel-via-banner UX.
        private CancellationTokenSource? _metadataRefreshCts;

        /// <summary>
        /// Kicks off (or resumes) a background metadata refresh for the given
        /// console using ArtworkFetchService. Surfaces progress in the notification
        /// banner and makes the banner clickable to cancel. Idempotent — if a
        /// refresh is already running, no-op.
        /// </summary>
        private void StartMetadataRefresh(string console)
        {
            if (_metadataRefreshCts != null && !_metadataRefreshCts.IsCancellationRequested)
                return; // already running

            var cts = new CancellationTokenSource();
            _metadataRefreshCts = cts;

            _ = Task.Run(async () =>
            {
                Dispatcher.Invoke(() =>
                {
                    _vm.NotificationText = $"Refreshing {console} metadata… (click to stop)";
                    _vm.IsNotification   = true;
                });
                try
                {
                    await _artworkFetch.RefreshConsoleMetadataAsync(console, msg =>
                    {
                        _vm.NotificationText = msg + " (click to stop)";
                        _vm.IsNotification   = true;
                    }, cts.Token);

                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!cts.IsCancellationRequested)
                            _vm.NotificationText = $"Refresh complete — metadata pass finished for {console}.";
                        _vm.IsNotification = true;
                        _ = Task.Delay(5000).ContinueWith(_ =>
                            Dispatcher.BeginInvoke(() =>
                            {
                                _vm.IsNotification   = false;
                                _vm.NotificationText = "";
                            }));
                    });
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _vm.NotificationText = $"Metadata refresh failed: {ex.Message}";
                        _vm.IsNotification   = true;
                        _ = Task.Delay(5000).ContinueWith(_ =>
                            Dispatcher.BeginInvoke(() =>
                            {
                                _vm.IsNotification   = false;
                                _vm.NotificationText = "";
                            }));
                    });
                }
                finally
                {
                    if (ReferenceEquals(_metadataRefreshCts, cts))
                        _metadataRefreshCts = null;
                }
            });
        }

        /// <summary>
        /// At startup, scan EVERY console in the library for games with incomplete
        /// metadata and silently kick off a resume pass. Processes consoles in
        /// sequence under a single cancellation token so clicking the banner stops
        /// the whole sweep. No-op if every console is already fully populated.
        ///
        /// ScreenScraper is now the primary metadata source (when configured) for
        /// all consoles, so any library — cartridge or disc-era, classic or
        /// recent — benefits from auto-resume picking up where it left off.
        /// </summary>
        private void AutoResumeArcadeMetadataRefresh()
        {
            try
            {
                // Snapshot all games once, group by console, find those with at
                // least one missing-meta entry. Stable order so the resume always
                // proceeds the same way across launches.
                var allGames = _db.GetAllGames();
                // Only count games that are BOTH missing fields AND haven't been tried yet.
                // Without the MetadataAttempts gate, libraries with un-fillable entries
                // (no ScreenScraper / OpenVGDB match) trigger the banner every launch
                // even though the inner pass short-circuits with zero work.
                var consolesNeedingMeta = allGames
                    .GroupBy(g => g.Console)
                    .Where(grp => !string.IsNullOrWhiteSpace(grp.Key)
                        && grp.Any(g => g.MetadataAttempts < 1
                                     && (string.IsNullOrWhiteSpace(g.Developer)
                                      || string.IsNullOrWhiteSpace(g.Genre)
                                      || string.IsNullOrWhiteSpace(g.Description)
                                      || g.Year == 0)))
                    .Select(grp => grp.Key)
                    .OrderBy(c => c)
                    .ToList();

                if (consolesNeedingMeta.Count == 0) return;
                if (_metadataRefreshCts != null && !_metadataRefreshCts.IsCancellationRequested) return;

                var cts = new CancellationTokenSource();
                _metadataRefreshCts = cts;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        foreach (string console in consolesNeedingMeta)
                        {
                            if (cts.IsCancellationRequested) break;
                            Dispatcher.Invoke(() =>
                            {
                                _vm.NotificationText = $"Resuming {console} metadata refresh… (click to stop)";
                                _vm.IsNotification   = true;
                            });
                            await _artworkFetch.RefreshConsoleMetadataAsync(console, msg =>
                            {
                                _vm.NotificationText = msg + " (click to stop)";
                                _vm.IsNotification   = true;
                            }, cts.Token);
                        }
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (!cts.IsCancellationRequested)
                                _vm.NotificationText = "Metadata refresh complete.";
                            _vm.IsNotification = true;
                            _ = Task.Delay(5000).ContinueWith(_ =>
                                Dispatcher.BeginInvoke(() =>
                                {
                                    _vm.IsNotification   = false;
                                    _vm.NotificationText = "";
                                }));
                        });
                    }
                    finally
                    {
                        if (ReferenceEquals(_metadataRefreshCts, cts))
                            _metadataRefreshCts = null;
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[AutoResume] {ex.Message}");
            }
        }

        private void InitializeControllerManager()
        {
            if (_controllerManager == null && App.Configuration != null)
            {
                _controllerManager = new ControllerManager(App.Configuration);
            }
        }

        private Task FetchMissingArtworkForConsoleAsync(string console, string displayName)
            => _artworkFetch.FetchMissingArtworkForConsoleAsync(console, displayName);

        private Task Fetch3DBoxArtForConsoleAsync(string console, string displayName)
            => _artworkFetch.Fetch3DBoxArtForConsoleAsync(console, displayName);

        private Task FetchScreenScraperArtForConsoleAsync(string console, string displayName)
            => _artworkFetch.FetchScreenScraperArtForConsoleAsync(console, displayName);

        // ── Game grid scrolling ───────────────────────────────────────────────
        // Override mouse wheel on both views so the system WheelScrollLines setting
        // is respected and scaled to card-appropriate pixel sizes (~80px per line).

        private void GameGridView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // The ListBox owns its internal ScrollViewer — find it and drive it directly.
            var sv = FindVisualChild<ScrollViewer>((DependencyObject)sender);
            if (sv == null) return;
            double lines = e.Delta / 120.0 * SystemParameters.WheelScrollLines;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - lines * 80);
            e.Handled = true;
        }

        private void GameListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // CanContentScroll=True with ScrollUnit=Item means VerticalOffset is in item units.
            // Scroll 3 items per wheel notch (standard Windows feel).
            var sv = FindVisualChild<ScrollViewer>((DependencyObject)sender);
            if (sv == null) return;
            double items = e.Delta / 120.0 * SystemParameters.WheelScrollLines;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - items);
            e.Handled = true;
        }

        private void LibraryView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            double lines = e.Delta / 120.0 * SystemParameters.WheelScrollLines;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - lines * 80);
            e.Handled = true;
        }

        /// <summary>
        /// Scrolls all library views (grid, grouped, list) to the top.
        /// Called on initial load only.
        /// </summary>
        private void ScrollLibraryToTop()
        {
            var gridSv = FindVisualChild<ScrollViewer>(GameGridView);
            gridSv?.ScrollToTop();
            LibraryView?.ScrollToTop();
            var listSv = FindVisualChild<ScrollViewer>(GameListView);
            listSv?.ScrollToTop();
        }

        private void SaveScrollPosition(string tag)
        {
            var sv = FindActiveScrollViewer();
            if (sv != null)
                _scrollPositions[tag] = sv.VerticalOffset;
        }

        private void RestoreScrollPosition(string tag)
        {
            if (_scrollPositions.TryGetValue(tag, out double offset))
            {
                var sv = FindActiveScrollViewer();
                sv?.ScrollToVerticalOffset(offset);
            }
            else
            {
                // First visit — start at top
                var sv = FindActiveScrollViewer();
                sv?.ScrollToTop();
            }
        }

        private ScrollViewer? FindActiveScrollViewer()
        {
            if (FavoritesGroupedView.Visibility == Visibility.Visible)
                return FavoritesGroupedView;
            if (GameListView.Visibility == Visibility.Visible)
                return FindVisualChild<ScrollViewer>(GameListView);
            if (LibraryView.Visibility == Visibility.Visible)
                return LibraryView;
            return FindVisualChild<ScrollViewer>(GameGridView);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        // ── Windows chrome mode ───────────────────────────────────────────────
        /// <summary>
        /// Applies Windows system chrome when the theme setting is on.
        /// Must be called after InitializeComponent() and before Show().
        /// </summary>
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ApplyWindowsChrome()
        {
            var theme = App.Configuration?.GetThemeConfiguration();
            if (theme?.UseWindowsChrome != true) return;

            // Switch to Windows system title bar. AllowsTransparency must be false
            // for WindowStyle other than None — change both before the HWND is created.
            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;

            // Strip the custom frameless styling from the outer border.
            OuterBorder.Margin = new Thickness(0);
            OuterBorder.CornerRadius = new CornerRadius(0);
            OuterBorder.BorderThickness = new Thickness(0);
            OuterBorder.Effect = null;

            // Hide the custom title bar row; system chrome provides its own.
            CustomTitleBar.Visibility = Visibility.Collapsed;
            RootGrid.RowDefinitions[0].Height = new GridLength(0);

            // Apply dark title bar to match the app theme once the HWND exists.
            SourceInitialized += (_, _) => ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            if (new WindowInteropHelper(this).Handle is var hwnd && hwnd != IntPtr.Zero)
            {
                // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 18985+ / Windows 11)
                int value = 1;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            }
        }

        // ── Background image ──────────────────────────────────────────────────
        /// <summary>
        /// Applies the user's background image behind the game grid.
        /// Call on startup and whenever theme settings change.
        /// </summary>
        public void ApplyBackgroundImage()
        {
            var theme = App.Configuration?.GetThemeConfiguration();
            // Storage form may be relative (under DataRoot in portable mode); resolve
            // to absolute before File.Exists / Uri construction.
            string bgPath = AppPaths.FromStoragePath(theme?.BackgroundImagePath ?? "");
            if (theme == null || string.IsNullOrWhiteSpace(bgPath)
                || !System.IO.File.Exists(bgPath))
            {
                GridBackgroundImage.Visibility = Visibility.Collapsed;
                GridBackgroundImage.Source = null;
                GridBackgroundTiled.Visibility = Visibility.Collapsed;
                GridBackgroundTiled.Fill = null;
                return;
            }

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(bgPath, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                double opacity = Math.Clamp(theme.BackgroundImageOpacity, 0.0, 1.0);

                if (theme.BackgroundImageRepeat)
                {
                    // Tiled mode — use ImageBrush on a Rectangle
                    GridBackgroundImage.Visibility = Visibility.Collapsed;
                    GridBackgroundImage.Source = null;

                    double zoom = Math.Clamp(theme.BackgroundImageZoom, 0.5, 5.0);
                    var brush = new System.Windows.Media.ImageBrush(bmp)
                    {
                        TileMode = System.Windows.Media.TileMode.Tile,
                        Stretch = System.Windows.Media.Stretch.None,
                        AlignmentX = System.Windows.Media.AlignmentX.Left,
                        AlignmentY = System.Windows.Media.AlignmentY.Top,
                        ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                        Viewport = new Rect(
                            theme.BackgroundImageOffsetX,
                            theme.BackgroundImageOffsetY,
                            bmp.PixelWidth * zoom,
                            bmp.PixelHeight * zoom),
                        Opacity = opacity,
                    };
                    brush.Freeze();

                    GridBackgroundTiled.Fill = brush;
                    GridBackgroundTiled.Visibility = Visibility.Visible;
                }
                else
                {
                    // Single image mode
                    GridBackgroundTiled.Visibility = Visibility.Collapsed;
                    GridBackgroundTiled.Fill = null;

                    GridBackgroundImage.Source = bmp;
                    GridBackgroundImage.Opacity = opacity;
                    GridBackgroundImage.Stretch = theme.BackgroundImageStretch switch
                    {
                        "Uniform" => System.Windows.Media.Stretch.Uniform,
                        "Fill" => System.Windows.Media.Stretch.Fill,
                        "None" => System.Windows.Media.Stretch.None,
                        _ => System.Windows.Media.Stretch.UniformToFill
                    };
                    double zoom = Math.Clamp(theme.BackgroundImageZoom, 0.5, 5.0);
                    BgImageScale.ScaleX = zoom;
                    BgImageScale.ScaleY = zoom;
                    BgImageTranslate.X = theme.BackgroundImageOffsetX;
                    BgImageTranslate.Y = theme.BackgroundImageOffsetY;

                    GridBackgroundImage.Visibility = Visibility.Visible;
                }

                // Override BgPrimaryBrush in the content area so the image is the
                // sole background — no theme color sitting on top of or behind it.
                GameContentGrid.Background = Brushes.Transparent;
                if (GameContentGrid.Parent is Grid contentGrid)
                    contentGrid.Background = Brushes.Transparent;
            }
            catch
            {
                GridBackgroundImage.Visibility = Visibility.Collapsed;
                GridBackgroundImage.Source = null;
                GridBackgroundTiled.Visibility = Visibility.Collapsed;
                GridBackgroundTiled.Fill = null;
            }
        }

        // ── Window chrome ──
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) MaximizeRestore();
            else DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => MaximizeRestore();

        private void MaximizeRestore()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        // ── Main window size/position persistence ──
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveMainWindowBounds();
        }

        private void RestoreMainWindowBounds()
        {
            try
            {
                var cfg = App.Configuration;
                if (cfg == null) return;

                double w = cfg.GetValue("mainWinWidth",  0.0);
                double h = cfg.GetValue("mainWinHeight", 0.0);
                double x = cfg.GetValue("mainWinLeft",   double.NaN);
                double y = cfg.GetValue("mainWinTop",    double.NaN);
                bool maximized = cfg.GetValue("mainWinMaximized", false);

                if (w >= MinWidth && h >= MinHeight)
                {
                    Width  = w;
                    Height = h;
                }
                if (!double.IsNaN(x) && !double.IsNaN(y))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = x;
                    Top  = y;
                }
                if (maximized)
                    WindowState = WindowState.Maximized;
            }
            catch { }
        }

        private void SaveMainWindowBounds()
        {
            try
            {
                var cfg = App.Configuration;
                if (cfg == null) return;

                cfg.SetValue("mainWinMaximized", WindowState == WindowState.Maximized);
                if (WindowState == WindowState.Normal)
                {
                    cfg.SetValue("mainWinWidth",  Width);
                    cfg.SetValue("mainWinHeight", Height);
                    cfg.SetValue("mainWinLeft",   Left);
                    cfg.SetValue("mainWinTop",    Top);
                }
                _ = cfg.SaveAsync();
            }
            catch { }
        }

        // Close the game detail card when the user clicks anywhere in MainWindow.
        // GameCard_Click also closes it before opening a new one, so there's no conflict.
        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            _openDetailWindow?.Close();
            base.OnPreviewMouseDown(e);
        }

        // ── Drag and drop ──
        protected override void OnDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            // Reset the safety timer each time DragOver fires so it only
            // triggers if no DragOver event arrives for 1.5 s (e.g. OS cancelled the drag).
            if (_dragLeaveTimer == null)
            {
                _dragLeaveTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromSeconds(1.5) };
                _dragLeaveTimer.Tick += (_, _) =>
                {
                    _dragLeaveTimer.Stop();
                    DropOverlay.Visibility = Visibility.Collapsed;
                };
            }
            _dragLeaveTimer.Stop();
            _dragLeaveTimer.Start();
            e.Handled = true;
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            // WPF fires DragLeave every time the cursor crosses a child element boundary,
            // causing the overlay to flash. Only hide when the cursor truly leaves the window.
            var pos = e.GetPosition(this);
            if (pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight)
            {
                _dragLeaveTimer?.Stop();
                DropOverlay.Visibility = Visibility.Collapsed;
            }
            base.OnDragLeave(e);
        }

        protected override void OnDrop(DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                // Check for .emutheme files — install them as themes
                var themeFiles = files.Where(f => f.EndsWith(".emutheme", StringComparison.OrdinalIgnoreCase)).ToArray();
                var romFiles = files.Where(f => !f.EndsWith(".emutheme", StringComparison.OrdinalIgnoreCase)).ToArray();

                foreach (var tf in themeFiles)
                {
                    var id = Services.ThemeService.Instance.InstallTheme(tf);
                    if (id != null)
                    {
                        MessageBox.Show($"Theme installed! Select it in Preferences > Theme.",
                            "Theme Installed", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }

                if (romFiles.Length > 0)
                {
                    _importer.ImportFilesAsync(romFiles, ResolveImportConsoleHint());
                }
            }
            base.OnDrop(e);
        }

        // ── Section collapse/expand ──
        private void ToggleSection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string sectionName = btn.Tag?.ToString() ?? "";
            var section = FindName(sectionName) as StackPanel;
            if (section == null) return;

            string arrowName = sectionName.Replace("Section", "Arrow");
            var arrow = FindName(arrowName) as TextBlock;

            bool isCollapsed = section.Visibility == Visibility.Collapsed;
            section.Visibility = isCollapsed ? Visibility.Visible : Visibility.Collapsed;
            if (arrow != null)
                arrow.Text = isCollapsed ? "▾" : "▸";
        }

        // ── Navigation ──
        private void SelectNavButton(Button btn)
        {
            if (_selectedNavButton != null)
            {
                _selectedNavButton.Background = System.Windows.Media.Brushes.Transparent;
                _selectedNavButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                ClearNavCount(_selectedNavButton);
            }
            _selectedNavButton = btn;
            btn.Background = (System.Windows.Media.Brush)FindResource("BgQuaternaryBrush");
            btn.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        }

        private void ClearNavCount(Button btn)
        {
            if (btn.Content is StackPanel sp)
            {
                var countBlock = sp.Children.OfType<TextBlock>()
                    .FirstOrDefault(tb => tb.Tag?.ToString() == "NavCount");
                if (countBlock != null) sp.Children.Remove(countBlock);
            }
        }

        private void ShowNavCount(Button btn, int count)
        {
            if (btn.Content is not StackPanel sp) return;
            ClearNavCount(btn);
            sp.Children.Add(new TextBlock
            {
                Text = count.ToString("N0"),
                Tag  = "NavCount",
                VerticalAlignment = VerticalAlignment.Center,
                Margin   = new Thickness(6, 0, 0, 0),
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                Opacity = 0.9
            });
        }

        private async void GroupSeeAll_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is string console)
                await _vm.NavigateToConsoleCommand.ExecuteAsync(console);
        }

        /// <summary>
        /// Handles View-side effects after any ViewModel navigation command completes:
        /// sidebar highlight, scroll-to-top, game count badge, box-art toggle.
        /// </summary>
        private void OnNavigated(string tag)
        {
            bool isConsoleView = !string.IsNullOrEmpty(tag)
                && tag != "All Games" && tag != "Recent" && tag != "Favorites"
                && tag != "RecentlyAdded" && !tag.StartsWith("Collection:");

            // Find the sidebar button that matches this navigation target
            Button? navBtn = FindSidebarButton(tag);
            if (navBtn != null)
                SelectNavButton(navBtn);

            // Toggle between favorites grouped view and normal library grid.
            // ApplyCurrentViewMode() is the single source of truth for the four
            // possible content panels (FavoritesGroupedView / GameGridView /
            // LibraryView / GameListView) — same logic lives in ViewToggle_Click
            // so navigation never desyncs from the user's grid/list choice.
            if (tag == "Favorites")
            {
                ApplyCurrentViewMode(forceFavorites: true);
                PopulateFavoritesView();
            }
            else
            {
                ApplyCurrentViewMode(forceFavorites: false);
            }

            // Save scroll position for the view we're leaving, restore for the one we're entering
            SaveScrollPosition(_currentNavTag);
            _currentNavTag = tag;

            // Hide content during scroll restore to avoid visible jump from top
            if (_scrollPositions.ContainsKey(tag))
            {
                GameContentGrid.Opacity = 0;
                Dispatcher.InvokeAsync(() =>
                {
                    RestoreScrollPosition(tag);
                    GameContentGrid.Opacity = 1;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }

            UpdateBoxArtToggleVisibility();
            UpdateSpacingControl(tag, isConsoleView);

            // Show per-console game count badge
            if (navBtn != null && isConsoleView)
            {
                ShowNavCount(navBtn, _vm.Games.Count);

                // Derive display name from the button content for toolbar
                string name = navBtn.Content is StackPanel sp
                    ? sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? tag
                    : tag;
                _vm.ToolbarTitle = name;
            }
        }

        // ── Per-console card spacing (toolbar slider) ─────────────────────────

        /// <summary>"H" or "V" — which axis the toolbar slider currently drives.</summary>
        private string _spacingAxis = "H";
        /// <summary>Suppress slider ValueChanged side-effects while we programmatically reload its value on navigation.</summary>
        private bool _spacingControlSuppressEvents;

        /// <summary>Show/hide the toolbar spacing control on navigation and load the active console's values.</summary>
        private void UpdateSpacingControl(string tag, bool isConsoleView)
        {
            if (SpacingControlPanel == null) return;
            SpacingControlPanel.Visibility = isConsoleView ? Visibility.Visible : Visibility.Collapsed;
            if (!isConsoleView) return;

            var (h, v) = GetPerConsoleSpacing(tag);
            ApplyCardSpacing(h, v);
            ReloadSpacingSliderValue(h, v);
        }

        /// <summary>Tap the H/V cap to flip the slider's active axis.</summary>
        private void SpacingHVToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _spacingAxis = _spacingAxis == "H" ? "V" : "H";
            SpacingHVLabel.Text = _spacingAxis;
            // Reload the slider to show the OTHER axis's current value for this console.
            var (h, v) = GetPerConsoleSpacing(_currentNavTag);
            ReloadSpacingSliderValue(h, v);
        }

        /// <summary>Slider drag writes the new value back to per-console config for the active axis.</summary>
        private void SpacingSliderToolbar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_spacingControlSuppressEvents) return;
            if (!IsConsoleTag(_currentNavTag)) return;

            var (h, v) = GetPerConsoleSpacing(_currentNavTag);
            int newVal = (int)Math.Round(e.NewValue);
            if (_spacingAxis == "H") h = newVal;
            else                     v = newVal;

            // Persist
            var theme = App.Configuration?.GetThemeConfiguration();
            if (theme != null)
            {
                theme.PerConsoleSpacing[_currentNavTag] = $"{h},{v}";
                App.Configuration!.SetThemeConfiguration(theme);
                _ = App.Configuration.SaveAsync();
            }

            ApplyCardSpacing(h, v);
        }

        /// <summary>Read per-console (H, V) from config, falling back to the global CardSpacing for both axes.</summary>
        private (int H, int V) GetPerConsoleSpacing(string console)
        {
            var theme = App.Configuration?.GetThemeConfiguration();
            int fallback = Math.Clamp(theme?.CardSpacing ?? 20, 4, 96);
            if (theme == null || string.IsNullOrEmpty(console)) return (fallback, fallback);
            if (theme.PerConsoleSpacing != null
                && theme.PerConsoleSpacing.TryGetValue(console, out var raw)
                && raw.Split(',') is var parts && parts.Length == 2
                && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int v))
            {
                return (Math.Clamp(h, 4, 96), Math.Clamp(v, 4, 96));
            }
            return (fallback, fallback);
        }

        /// <summary>Push the H/V values into the LibraryCardMargin resource so the grid re-lays-out.</summary>
        private void ApplyCardSpacing(int h, int v)
        {
            Application.Current.Resources["LibraryCardMargin"] = new Thickness(0, 0, h, v);
        }

        /// <summary>Reload the slider's displayed value from per-console state without re-firing ValueChanged.</summary>
        private void ReloadSpacingSliderValue(int h, int v)
        {
            if (SpacingSliderToolbar == null) return;
            _spacingControlSuppressEvents = true;
            SpacingSliderToolbar.Value = _spacingAxis == "H" ? h : v;
            _spacingControlSuppressEvents = false;
        }

        private static bool IsConsoleTag(string tag)
            => !string.IsNullOrEmpty(tag)
               && tag != "All Games" && tag != "Recent"
               && tag != "Favorites" && tag != "RecentlyAdded"
               && !tag.StartsWith("Collection:");

        private Button? FindSidebarButton(string tag)
        {
            // Check console buttons in the sidebar panel
            // Console buttons use CommandParameter (not Tag) after MVVM migration
            string? GetButtonTag(Button b) => (b.CommandParameter as string) ?? (b.Tag as string);

            foreach (var child in SidebarPanel.Children.OfType<FrameworkElement>())
            {
                if (child is Button btn && GetButtonTag(btn) == tag)
                    return btn;
                if (child is StackPanel sp)
                {
                    foreach (var nested in sp.Children.OfType<Button>())
                    {
                        if (GetButtonTag(nested) == tag)
                            return nested;
                    }
                }
            }

            // Check special nav buttons
            if (tag == "All Games") return FindName("NavAllGames") as Button
                ?? SidebarPanel.Children.OfType<Button>()
                    .FirstOrDefault(b => b.Content?.ToString()?.Contains("All Games") == true);
            if (tag == "Recent") return SidebarPanel.Children.OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString()?.Contains("Recently Played") == true);
            if (tag == "Favorites") return SidebarPanel.Children.OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString()?.Contains("Favorites") == true);
            if (tag == "RecentlyAdded") return SidebarPanel.Children.OfType<Button>()
                .FirstOrDefault(b => b.Content?.ToString()?.Contains("Recently Added") == true);
            if (tag.StartsWith("Collection:") && int.TryParse(tag.AsSpan(11), out int colId))
                return UserCollectionsPanel.Children.OfType<Button>()
                    .FirstOrDefault(b => b.Tag is int id && id == colId);

            return null;
        }

        private void SidebarPanel_RightClick(object sender, MouseButtonEventArgs e)
        {
            // Walk up from the element that was clicked to find a Button with a Tag
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source != SidebarPanel)
            {
                if (source is Button btn && btn.Tag is int collectionId)
                {
                    e.Handled = true;
                    string displayName = btn.Content?.ToString()?.Replace("📂  ", "") ?? "Collection";
                    var menu = new ContextMenu();

                    var renameItem = new MenuItem { Header = "✏  Rename Collection" };
                    renameItem.Click += (_, _) =>
                    {
                        var dialog = new RenameWindow(displayName) { Owner = this };
                        if (dialog.ShowDialog() == true)
                        {
                            _db.RenameCollection(collectionId, dialog.NewTitle);
                            RefreshCollectionsSidebar();
                        }
                    };
                    menu.Items.Add(renameItem);

                    var deleteItem = new MenuItem { Header = "🗑  Delete Collection" };
                    deleteItem.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                        .ConvertFromString("#FF5F57")!);
                    deleteItem.Click += (_, _) =>
                    {
                        var dlg = new ConfirmDialog(
                            "Delete Collection",
                            $"Delete the collection \"{displayName}\"?\n\nGames will not be removed from your library.",
                            confirmLabel: "Delete")
                        { Owner = this };
                        if (dlg.ShowDialog() != true) return;
                        _db.DeleteCollection(collectionId);
                        RefreshCollectionsSidebar();
                    };
                    menu.Items.Add(deleteItem);

                    menu.PlacementTarget = btn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    menu.IsOpen = true;
                    return;
                }

                if (source is Button consoleBtn
                    && ((consoleBtn.CommandParameter as string) ?? (consoleBtn.Tag as string)) is string console
                    && !string.IsNullOrEmpty(console))
                {
                    e.Handled = true;
                    string displayName = console;
                    // Try to get a friendly name from the button content
                    if (consoleBtn.Content is StackPanel sp)
                    {
                        var tb = sp.Children.OfType<TextBlock>().LastOrDefault();
                        if (tb != null) displayName = tb.Text;
                    }
                    else if (consoleBtn.Content is string s)
                    {
                        displayName = s;
                    }

                    int count = _db.GetGameCountForConsole(console);

                    var menu = new ContextMenu();

                    // Refresh Library — always available, even for empty consoles.
                    // Rescans the configured library folder so ROMs dropped in
                    // outside Emutastic show up without a full re-import.
                    var refreshItem = new MenuItem { Header = "🔄  Refresh Library" };
                    refreshItem.Click += (_, _) => RefreshLibraryFolder(console);
                    menu.Items.Add(refreshItem);

                    if (count == 0)
                    {
                        // Empty console: just the refresh action — no "remove all" or
                        // artwork-fetch options to show.
                        menu.PlacementTarget = consoleBtn;
                        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                        menu.IsOpen = true;
                        return;
                    }

                    menu.Items.Add(new Separator());

                    var item = new MenuItem
                    {
                        Header = $"🗑  Remove all {displayName} games ({count})"
                    };
                    item.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                        .ConvertFromString("#FF5F57")!);
                    item.Click += (_, _) =>
                    {
                        var dlg = new ConfirmDialog(
                            "Remove All Games",
                            $"Remove all {count} {displayName} games from your library?\n\nYour save states will not be affected.",
                            confirmLabel: "Remove All")
                        { Owner = this };
                        if (dlg.ShowDialog() != true) return;
                        _db.DeleteAllGamesForConsole(console);
                        _ = ReloadAndFilterAsync();
                    };
                    menu.Items.Add(item);

                    var artItem = new MenuItem { Header = "⬇  Download Missing Artwork" };
                    artItem.Click += async (_, _) => await FetchMissingArtworkForConsoleAsync(console, displayName);
                    menu.Items.Add(artItem);

                    var snapConfig = App.Configuration?.GetSnapConfiguration();
                    if (snapConfig is { ScreenScraperEnabled: true }
                        && !string.IsNullOrWhiteSpace(snapConfig.ScreenScraperUser))
                    {
                        var art3DItem = new MenuItem { Header = "⬇  Download 3D Box Art" };
                        art3DItem.Click += async (_, _) => await Fetch3DBoxArtForConsoleAsync(console, displayName);
                        menu.Items.Add(art3DItem);

                        var ss2DItem = new MenuItem { Header = "⬇  Download ScreenScraper 2D Art" };
                        ss2DItem.Click += async (_, _) => await FetchScreenScraperArtForConsoleAsync(console, displayName);
                        menu.Items.Add(ss2DItem);
                    }
                    var editControlsItem = new MenuItem { Header = "🎮  Edit Controls…" };
                    editControlsItem.Click += (_, _) =>
                    {
                        var win = new Views.PreferencesWindow(_db, _controllerManager!, App.Configuration!,
                            initialConsole: console)
                        { Owner = this };
                        win.ShowDialog();
                    };
                    menu.Items.Insert(0, editControlsItem);
                    menu.Items.Insert(1, new Separator());

                    menu.PlacementTarget = consoleBtn;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    menu.IsOpen = true;
                    return;
                }
                source = VisualTreeHelper.GetParent(source);
            }
        }

        private void NavUserCollection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int collectionId) return;
            string displayName = btn.Content?.ToString()?.Replace("📂  ", "") ?? "Collection";
            _vm.ToolbarTitle = displayName;
            _vm.NavigateToCollectionCommand.Execute(collectionId);
        }

        public void RefreshCollectionsSidebar()
        {
            UserCollectionsPanel.Children.Clear();
            foreach (var (id, name) in _db.GetAllCollections())
            {
                var btn = new Button
                {
                    Content = $"📂  {name}",
                    Style = (Style)FindResource("SidebarItemStyle"),
                    Tag = id
                };
                btn.Click += NavUserCollection_Click;
                UserCollectionsPanel.Children.Add(btn);
            }
        }

        // ── Controller hot-plug status poll ───────────────────────────────────
        // Diff the connected-controller list every 2 seconds. On change, surface
        // a 15-second toast in the existing StatusText. First poll after launch
        // primes _lastConnectedControllers without emitting a "connected" message
        // (those controllers were there before the app started — not events).
        private System.Windows.Threading.DispatcherTimer? _controllerStatusTimer;
        private List<string>? _lastConnectedControllers;

        private void StartControllerStatusPoll()
        {
            _controllerStatusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _controllerStatusTimer.Tick += (_, _) =>
            {
                List<string> current;
                try { current = Services.ControllerManager.GetConnectedControllers(); }
                catch { return; }

                if (_lastConnectedControllers == null)
                {
                    // Initial reading — nothing to compare against, no event.
                    _lastConnectedControllers = current;
                    return;
                }

                // Diff: anything in `current` not in last = newly connected.
                //       anything in last not in `current` = disconnected.
                var added = current.Except(_lastConnectedControllers, StringComparer.Ordinal).ToList();
                var removed = _lastConnectedControllers.Except(current, StringComparer.Ordinal).ToList();
                _lastConnectedControllers = current;

                foreach (var name in added)
                    _vm.SetStatus($"Controller connected: {name}", 15000);
                foreach (var name in removed)
                    _vm.SetStatus($"Controller disconnected: {name}", 15000);
            };
            _controllerStatusTimer.Start();
        }

        private void NavPreferences_Click(object sender, RoutedEventArgs e)
        {
            InitializeControllerManager();
            var prefs = new PreferencesWindow(_db, _controllerManager!, App.Configuration!) { Owner = this };
            bool ss2dBefore = Game.PreferScreenScraper2D;
            prefs.ShowDialog();
            // If the ScreenScraper 2D preference changed, refresh the grid so cards show updated art
            if (Game.PreferScreenScraper2D != ss2dBefore)
            {
                _vm.InvalidateCache();
                _vm.RefreshAllGames();
            }
        }

        private void NavImport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Import ROMs",
                Filter = "ROM Files|*.nes;*.sfc;*.smc;*.z64;*.n64;*.gb;*.gbc;*.gba;*.nds;*.md;*.gen;*.sms;*.gg;*.pce;*.iso;*.pbp;*.cso;*.a26;*.a52;*.a78;*.lnx;*.zip;*.7z|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                _importer.ImportFilesAsync(dialog.FileNames, ResolveImportConsoleHint());
            }
        }

        /// <summary>
        /// Refresh ONE console's library: rescan the folders that currently
        /// contain that console's existing games, picking up new ROMs the
        /// user dropped in alongside them. Filters by the console's own
        /// extension set so a single "all ROMs in one folder" layout doesn't
        /// drag every other console's files through the importer when the
        /// user just wants SNES updated.
        /// Reuses the standard import pipeline for scan/progress status; adds
        /// a one-shot drain override that swaps "Import complete" for
        /// "Refresh complete — added N new {console} games" so users know
        /// whether the rescan actually found anything.
        /// </summary>
        private void RefreshLibraryFolder(string console)
        {
            // Parent dirs of THIS console's already-imported games. Prefer
            // OriginalSourcePath when present — for zipped imports RomPath
            // points at the post-extraction file under [DataRoot]\ExtractedRoms\
            // which is internal storage, not the user's actual collection
            // folder. Fall back to RomPath's directory for legacy entries
            // imported before source-path tracking.
            var scanDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in _db.GetAllGames())
            {
                if (!string.Equals(game.Console, console, StringComparison.OrdinalIgnoreCase)) continue;
                string source = !string.IsNullOrEmpty(game.OriginalSourcePath)
                    ? game.OriginalSourcePath
                    : game.RomPath;
                if (string.IsNullOrEmpty(source)) continue;
                try
                {
                    string? dir = Path.GetDirectoryName(source);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        scanDirs.Add(dir);
                }
                catch { /* malformed path — skip */ }
            }

            if (scanDirs.Count == 0)
            {
                MessageBox.Show(
                    $"Nothing to refresh — no {console} games' folders could be located on disk.",
                    "Refresh Library", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Console-specific extension set so a flat all-roms-in-one-folder
            // layout doesn't pull every console through the importer.
            var consoleExts = new HashSet<string>(
                Services.RomService.GetExtensionsForConsole(console),
                StringComparer.OrdinalIgnoreCase);
            if (consoleExts.Count == 0)
            {
                MessageBox.Show(
                    $"No file extensions are registered for {console}, so nothing can be refreshed.",
                    "Refresh Library", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Enumerate candidate files (recursive — handles per-game
            // subfolders like .../FF7/Disc1.cue layouts).
            var candidates = new List<string>();
            foreach (var dir in scanDirs)
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        if (consoleExts.Contains(Path.GetExtension(path)))
                            candidates.Add(path);
                    }
                }
                catch { /* dir may have moved — skip */ }
            }

            if (candidates.Count == 0)
            {
                MessageBox.Show(
                    $"Nothing to refresh — no {console} candidate files found in your existing {console} folders.",
                    "Refresh Library", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Per-console count, not total — a zipped folder may contain ROMs
            // for multiple consoles and the importer classifies by inner
            // contents. When the user clicked Refresh on SNES, only count
            // SNES games added so the message stays truthful.
            int sameConsoleBefore = _db.GetAllGames().Count(g =>
                string.Equals(g.Console, console, StringComparison.OrdinalIgnoreCase));

            Action? onDrained = null;
            onDrained = () =>
            {
                if (_importer != null) _importer.ImportQueueDrained -= onDrained;
                Dispatcher.BeginInvoke(() =>
                {
                    int sameConsoleAfter = _db.GetAllGames().Count(g =>
                        string.Equals(g.Console, console, StringComparison.OrdinalIgnoreCase));
                    int added = Math.Max(0, sameConsoleAfter - sameConsoleBefore);
                    string addedMsg = added switch
                    {
                        0 => $"Refresh — no new {console} ROMs.",
                        1 => $"Refresh — added 1 new {console} game.",
                        _ => $"Refresh — added {added} new {console} games.",
                    };
                    SetStatus(addedMsg);

                    // Metadata pass: backfill missing fields for existing entries on
                    // this console. The user clicked Refresh expecting their library
                    // to be refreshed — that means metadata too, not just new files.
                    // Reset MetadataAttempts for this console so games previously
                    // marked as "we tried, came back empty" re-enter the queue.
                    // The manual Refresh click IS the deliberate user opt-in to
                    // re-trying those games.
                    _db.ResetMetadataAttemptsForConsole(console);
                    StartMetadataRefresh(console);
                });
            };
            _importer.ImportQueueDrained += onDrained;

            // Hand the importer a pre-filtered file list (not folders) so it
            // doesn't enumerate-then-import unrelated extensions itself. Pass
            // the console as the hint so any ambiguous-extension files in the
            // batch (.m3u / .cue / .chd / .iso) auto-resolve to this console
            // instead of triggering the picker — the user already told us which
            // library this is by right-clicking that console's Refresh action.
            _importer.ImportFilesAsync(candidates, hintedConsole: console);
        }

        /// <summary>
        /// Returns the user's currently-selected console as a hint for the importer,
        /// or null when on a non-console view ("All Games", "Recent Games",
        /// "Favorites", any user-collection, etc.).
        ///
        /// Source of truth is <c>_currentNavTag</c> (the actual active nav button),
        /// not <c>_vm.SelectedConsole</c> — non-console navs (Recent / Favorites /
        /// Collections) don't reset SelectedConsole, so it can stale-stick on the
        /// last console the user visited and produce the wrong hint.
        ///
        /// Hint is also validated against <c>RomService.IsKnownConsoleTag</c> so a
        /// future nav-tag change can't slip a non-console string through to the
        /// importer (which would tag every dropped file with that bogus value).
        /// </summary>
        private string? ResolveImportConsoleHint()
        {
            string tag = _currentNavTag;
            if (!Services.RomService.IsKnownConsoleTag(tag)) return null;
            return tag;
        }

        private void SetStatus(string msg, bool autoClear = false)
            => _vm.SetStatus(msg, autoClear);

        // ── List view (OpenEmu-style table) ──────────────────────────────────
        // Double-click a row to launch (matches OpenEmu); single-click just selects.
        private void GameListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (GameListView.SelectedItem is not Game game) return;
            _openDetailWindow?.Close();
            _openDetailWindow = new GameDetailWindow(game) { Owner = this };
            _openDetailWindow.Closed += (_, _) => { _openDetailWindow = null; };
            _openDetailWindow.Show();
        }

        private void GameListView_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject src) return;
            // Walk up to the row container so we can read its DataContext (a Game).
            var row = FindAncestor<ListViewItem>(src);
            if (row?.DataContext is not Game game) return;
            row.IsSelected = true;
            bool isMultiSelect = GameListView.SelectedItems.Count > 1
                              && GameListView.SelectedItems.Contains(game);
            var menu = isMultiSelect
                ? BuildMultiSelectContextMenu(GameListView.SelectedItems.OfType<Game>().ToList())
                : BuildContextMenu(game);
            menu.PlacementTarget = row;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        // Column-header click → set sort. Single-column sort, click again toggles
        // direction. Persisted via the config service so the user's choice
        // survives across launches (matches OpenEmu's NSUserDefaults behavior).
        private GridViewColumnHeader? _activeSortHeader;
        private System.ComponentModel.ListSortDirection _activeSortDirection
            = System.ComponentModel.ListSortDirection.Ascending;

        private void GameListColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header) return;
            if (header.Role == GridViewColumnHeaderRole.Padding) return;
            string sortMember = HeaderToSortMember(header.Column?.Header as string ?? "");
            if (string.IsNullOrEmpty(sortMember)) return;

            // Same column clicked → flip direction; new column → reset to ascending.
            var dir = (header == _activeSortHeader
                       && _activeSortDirection == System.ComponentModel.ListSortDirection.Ascending)
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending;
            ApplyListSort(sortMember, dir, header);
            App.Configuration?.SetValue("listSortColumn", sortMember);
            App.Configuration?.SetValue("listSortDirection", dir.ToString());
        }

        private static string HeaderToSortMember(string headerLabel) => headerLabel switch
        {
            "Name"        => "Title",   // header label changed; Game.Title is the underlying property
            "Title"       => "Title",   // legacy persisted value from prior builds
            "Rating"      => "Rating",
            "Last Played" => "LastPlayed",
            "System"      => "Console",
            _             => "",
        };

        private void ApplyListSort(string sortMember, System.ComponentModel.ListSortDirection dir,
            GridViewColumnHeader? header)
        {
            // GameListView's ItemsSource is bound to the {StaticResource GameListSource}
            // CollectionViewSource defined in XAML. GetDefaultView on that source
            // returns its private view (NOT the default view shared with the grid),
            // so sort descriptions added here only affect the list view.
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GameListView.ItemsSource);
            if (view == null) return;
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(sortMember, dir));
            view.Refresh();

            // Update header chrome — clear tags on every header sibling (including
            // the trailing padding header WPF inserts), then mark the clicked one.
            if (header?.Parent is System.Windows.Controls.GridViewHeaderRowPresenter row)
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(row);
                for (int i = 0; i < n; i++)
                {
                    if (System.Windows.Media.VisualTreeHelper.GetChild(row, i) is GridViewColumnHeader h
                        && h.Role != GridViewColumnHeaderRole.Padding)
                        h.Tag = null;
                }
            }
            if (header != null)
                header.Tag = dir == System.ComponentModel.ListSortDirection.Ascending ? "SortAsc" : "SortDesc";
            _activeSortHeader = header;
            _activeSortDirection = dir;
        }

        // Restore persisted sort on first show of the list view. Defaults to
        // Title ascending (matches OpenEmu's default).
        private bool _listSortRestored;
        private void RestoreListSort()
        {
            if (_listSortRestored) return;
            string col = App.Configuration?.GetValue("listSortColumn", "Title") ?? "Title";
            string dirStr = App.Configuration?.GetValue("listSortDirection", "Ascending") ?? "Ascending";
            var dir = string.Equals(dirStr, "Descending", StringComparison.OrdinalIgnoreCase)
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending;
            GridViewColumnHeader? targetHeader = FindHeaderForColumn(col);
            // If the header presenter isn't realized yet (first show), retry
            // once at Background priority so layout completes first. Without
            // this the chevron stays missing until the user clicks a header
            // (the sort itself still applies via SortDescriptions).
            if (targetHeader == null && !_listSortRestored)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    targetHeader = FindHeaderForColumn(col);
                    ApplyListSort(col, dir, targetHeader);
                    _listSortRestored = true;
                }), System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            ApplyListSort(col, dir, targetHeader);
            _listSortRestored = true;
        }

        private GridViewColumnHeader? FindHeaderForColumn(string sortMember)
        {
            var presenter = FindVisualChild<System.Windows.Controls.GridViewHeaderRowPresenter>(GameListView);
            if (presenter == null) return null;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter);
            for (int i = 0; i < n; i++)
            {
                if (System.Windows.Media.VisualTreeHelper.GetChild(presenter, i) is GridViewColumnHeader h
                    && h.Role != GridViewColumnHeaderRole.Padding
                    && h.Column?.Header is string s
                    && HeaderToSortMember(s) == sortMember)
                    return h;
            }
            return null;
        }

        // ── View toggle (grid / list) ──
        private void ViewToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked) return;
            bool listActive = clicked.Tag?.ToString() == "List";
            ViewGrid.IsChecked = !listActive;
            ViewList.IsChecked = listActive;
            ApplyCurrentViewMode(forceFavorites: _vm.IsShowingFavorites);
        }

        /// <summary>
        /// Single source of truth for which content panel is visible. Reads the
        /// current grid/list toggle state plus whether the user is on the
        /// favorites view, then sets visibility on every panel that competes for
        /// the same screen real estate. Called from both <see cref="ViewToggle_Click"/>
        /// and <see cref="OnNavigated"/> so navigating between sections (e.g.
        /// All Games → Recently Added) can never leave the grid AND list views
        /// rendered simultaneously.
        /// </summary>
        private void ApplyCurrentViewMode(bool forceFavorites)
        {
            bool listActive = ViewList?.IsChecked == true;

            if (listActive)
            {
                // List always wins — the two grid views and the favorites grouped
                // panel must all be hidden. Strip any IsGroupedView bindings off
                // the grid views or they'd flip themselves Visible later.
                System.Windows.Data.BindingOperations.ClearBinding(GameGridView, VisibilityProperty);
                System.Windows.Data.BindingOperations.ClearBinding(LibraryView, VisibilityProperty);
                GameGridView.Visibility         = Visibility.Collapsed;
                LibraryView.Visibility          = Visibility.Collapsed;
                FavoritesGroupedView.Visibility = Visibility.Collapsed;
                GameListView.Visibility         = Visibility.Visible;
                // Restore the persisted sort once on first show — otherwise the
                // list comes up unsorted (insertion order from the DB query).
                Dispatcher.BeginInvoke(new Action(RestoreListSort),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            // Grid mode — list panel must be hidden.
            GameListView.Visibility = Visibility.Collapsed;

            if (forceFavorites)
            {
                // Favorites uses its own grouped panel; the IsGroupedView-bound
                // grid views must yield.
                System.Windows.Data.BindingOperations.ClearBinding(GameGridView, VisibilityProperty);
                System.Windows.Data.BindingOperations.ClearBinding(LibraryView, VisibilityProperty);
                FavoritesGroupedView.Visibility = Visibility.Visible;
                GameGridView.Visibility         = Visibility.Collapsed;
                LibraryView.Visibility          = Visibility.Collapsed;
                return;
            }

            // Normal grid — restore IsGroupedView bindings (one of GameGridView /
            // LibraryView is visible at a time depending on grouped state).
            FavoritesGroupedView.Visibility = Visibility.Collapsed;
            GameGridView.SetBinding(VisibilityProperty,
                new System.Windows.Data.Binding("IsGroupedView")
                { Converter = (System.Windows.Data.IValueConverter)FindResource("InverseBoolToVisibility") });
            LibraryView.SetBinding(VisibilityProperty,
                new System.Windows.Data.Binding("IsGroupedView")
                { Converter = (System.Windows.Data.IValueConverter)FindResource("BoolToVisibility") });
        }

        private void BoxArtToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked) return;
            bool use3D = clicked.Tag?.ToString() == "3D";
            BoxArt2D.IsChecked = !use3D;
            BoxArt3D.IsChecked = use3D;

            // In favorites view, toggle applies to all consoles shown
            if (_vm.IsShowingFavorites)
            {
                var consoles = _vm.Games.Select(g => g.Console).Distinct();
                foreach (var c in consoles)
                {
                    if (use3D) Game.EnableConsole3D(c);
                    else       Game.DisableConsole3D(c);
                }
            }
            else
            {
                string console = _vm.SelectedConsole ?? "";
                if (use3D) Game.EnableConsole3D(console);
                else       Game.DisableConsole3D(console);
            }

            // Persist preference
            var snapConfig = App.Configuration?.GetSnapConfiguration();
            if (snapConfig != null)
            {
                snapConfig.Use3DBoxArtConsoles = new System.Collections.Generic.List<string>(Game.Consoles3D);
                App.Configuration!.SetSnapConfiguration(snapConfig);
            }

            // Refresh only the current view
            _vm.RefreshAllGames();

            // Rebuild favorites view if active so art paths update
            if (FavoritesGroupedView.Visibility == Visibility.Visible)
                PopulateFavoritesView();
        }

        /// <summary>
        /// Shows the 2D/3D toggle if any game in the current view has 3D box art.
        /// Sets the toggle state based on the current console's preference.
        /// </summary>
        private void UpdateBoxArtToggleVisibility()
        {
            bool any3D = _vm.Games?.Any(g => !string.IsNullOrEmpty(g.BoxArt3DPath)) == true;
            BoxArtTogglePanel.Visibility = any3D ? Visibility.Visible : Visibility.Collapsed;

            if (any3D)
            {
                string console = _vm.SelectedConsole ?? "";
                bool is3D = Game.Consoles3D.Contains(console);
                BoxArt2D.IsChecked = !is3D;
                BoxArt3D.IsChecked = is3D;
            }
        }

        // ── Search ──
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb) _vm.SearchGames(tb.Text);
        }

        // ── Game card left click ──
        private void GameCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (sender is not FrameworkElement fe || fe.DataContext is not Game game) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift+click — range select, never open detail
                e.Handled = true;
                DoRangeSelect(game);
                return;
            }

            // Normal click — clear any selection, open detail, update anchor
            GameGridView.SelectedItems.Clear();
            _selectionAnchor = game;
            _openDetailWindow?.Close();
            _openDetailWindow = new GameDetailWindow(game) { Owner = this };
            _openDetailWindow.Closed += async (_, _) =>
            {
                _openDetailWindow = null;
                // If the game was removed via the detail card, refresh the view.
                if (!_db.GameExists(game.Id))
                {
                    _vm.RemoveGame(game);
                    await _vm.FilterGamesAsync();
                }
            };
            _openDetailWindow.Show();
        }

        private void DoRangeSelect(Game clicked)
        {
            var items = GameGridView.Items.Cast<Game>().ToList();
            int clickedIdx = items.IndexOf(clicked);
            if (clickedIdx < 0) return;

            // First Shift+click with no anchor — select just this game
            if (_selectionAnchor == null)
            {
                _selectionAnchor = clicked;
                GameGridView.SelectedItems.Clear();
                GameGridView.SelectedItems.Add(clicked);
                return;
            }

            int anchorIdx = items.IndexOf(_selectionAnchor);
            if (anchorIdx < 0) anchorIdx = 0;

            int start = Math.Min(anchorIdx, clickedIdx);
            int end   = Math.Max(anchorIdx, clickedIdx);

            GameGridView.SelectedItems.Clear();
            for (int i = start; i <= end; i++)
                GameGridView.SelectedItems.Add(items[i]);
        }

        // ── Game card right click ──
        private void GameCard_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement fe || fe.DataContext is not Game game) return;

            // If right-clicking a card that's part of a multi-selection, keep the
            // selection intact and show the bulk menu; otherwise treat as single-game.
            bool isMultiSelect = GameGridView.SelectedItems.Count > 1
                              && GameGridView.SelectedItems.Contains(game);

            var menu = isMultiSelect
                ? BuildMultiSelectContextMenu(GameGridView.SelectedItems.OfType<Game>().ToList())
                : BuildContextMenu(game);

            menu.PlacementTarget = fe;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private ContextMenu BuildMultiSelectContextMenu(List<Game> games)
        {
            var menu = new ContextMenu();
            var toDelete = games; // already captured
            menu.Items.Add(MakeMenuItem($"🗑  Delete Selected ({toDelete.Count})", async () =>
            {
                string msg = $"Delete {toDelete.Count} games? Save states will not be removed.";
                var confirm = new Views.ConfirmDialog("Delete Games", msg) { Owner = this };
                if (confirm.ShowDialog() != true) return;

                await Task.Run(() => _db.DeleteGames(toDelete.Select(g => g.Id)));
                foreach (var g in toDelete) _vm.RemoveGame(g);
                GameGridView.SelectedItems.Clear();
                _selectionAnchor = null;
                await _vm.FilterGamesAsync();
            }));
            return menu;
        }

        private ContextMenu BuildContextMenu(Game game)
        {
            var menu = new ContextMenu();

            // ── Play Game ──
            menu.Items.Add(MakeMenuItem("▶  Play Game", () =>
            {
                var detail = new GameDetailWindow(game) { Owner = this };
                detail.ShowDialog();
            }));

            // ── Play Save State submenu ──
            var saveStates = _db.GetSaveStatesByGame(game.Id);
            var saveStateItem = new MenuItem { Header = "⏱  Play Save State" };

            if (saveStates.Count == 0)
            {
                saveStateItem.Items.Add(new MenuItem { Header = "No save states", IsEnabled = false });
            }
            else
            {
                foreach (var s in saveStates.Take(10))
                {
                    var state = s;
                    var si = new MenuItem { Header = state.Name };
                    si.Click += (_, _) => LaunchWithSaveState(state);
                    saveStateItem.Items.Add(si);
                }
            }
            menu.Items.Add(saveStateItem);

            // ── Favorite toggle ──
            string favHeader = game.IsFavorite ? "♥  Remove from Favorites" : "♡  Add to Favorites";
            menu.Items.Add(MakeMenuItem(favHeader, () =>
            {
                game.IsFavorite = !game.IsFavorite;
                _db.ToggleFavorite(game.Id, game.IsFavorite);
                _vm.RefreshGame(game);
                if (_vm.IsShowingFavorites)
                    _vm.LoadFavorites(_db);
            }));

            menu.Items.Add(new Separator());

            // ── Rating submenu ──
            var ratingItem = new MenuItem { Header = "⭐  Rating" };
            var ratings = new[] {
                ("None",    0), ("★☆☆☆☆", 1), ("★★☆☆☆", 2),
                ("★★★☆☆", 3), ("★★★★☆", 4), ("★★★★★", 5)
            };
            foreach (var (label, value) in ratings)
            {
                int val = value;
                var ri = new MenuItem { Header = label, IsChecked = game.Rating == val };
                ri.Click += (s, ev) =>
                {
                    game.Rating = val;
                    _db.UpdateRating(game.Id, val);
                };
                ratingItem.Items.Add(ri);
            }
            menu.Items.Add(ratingItem);

            menu.Items.Add(new Separator());

            // ── Show in Explorer ──
            menu.Items.Add(MakeMenuItem("📁  Show in Explorer", () =>
            {
                if (File.Exists(game.RomPath))
                    System.Diagnostics.Process.Start("explorer.exe",
                        $"/select,\"{game.RomPath}\"");
                else
                    MessageBox.Show("ROM file not found.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }));

            menu.Items.Add(new Separator());

            // ── Download Cover Art ──
            menu.Items.Add(MakeMenuItem("⬇  Download Cover Art", async () =>
            {
                var (artworkPath, ssArtPath) = await _artworkFetch.FetchSingleGameArtworkAsync(game);
                if (artworkPath == null && ssArtPath == null)
                {
                    var dlg = new ConfirmDialog("Artwork", "Could not find artwork for this game.", "OK", danger: false) { Owner = this };
                    dlg.CancelBtn.Visibility = Visibility.Collapsed;
                    dlg.ShowDialog();
                }
            }));

            // ── Add Cover Art from File ──
            menu.Items.Add(MakeMenuItem("🖼  Add Cover Art from File", () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select Cover Art",
                    Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All Files|*.*"
                };
                if (dialog.ShowDialog() == true)
                {
                    string cacheFolder = AppPaths.GetFolder("Artwork", game.Console);
                    string ext = Path.GetExtension(dialog.FileName);
                    string destPath = Path.Combine(cacheFolder,
                        $"{game.RomHash}_custom{ext}");
                    File.Copy(dialog.FileName, destPath, overwrite: true);

                    _db.UpdateCoverArt(game.Id, destPath);
                    game.CoverArtPath = destPath;
                    _vm.RefreshGame(game);
                }
            }));

            menu.Items.Add(new Separator());

            // ── Add to Collection submenu (multi-select via checkboxes) ──
            var collectionItem = new MenuItem { Header = "📂  Add to Collection" };

            var allCollections = _db.GetAllCollections();
            var gameCollections = _db.GetCollectionsForGame(game.Id);
            var gameCollectionIds = new HashSet<int>(gameCollections.Select(c => c.Id));

            foreach (var (colId, colName) in allCollections)
            {
                int id = colId;
                var ci = new MenuItem
                {
                    Header = colName,
                    IsCheckable = true,
                    IsChecked = gameCollectionIds.Contains(id)
                };
                ci.Click += (_, _) =>
                {
                    if (ci.IsChecked)
                        _db.AddGameToCollection(game.Id, id);
                    else
                        _db.RemoveGameFromCollection(game.Id, id);
                    RefreshCollectionsSidebar();
                };
                collectionItem.Items.Add(ci);
            }

            if (allCollections.Count > 0)
                collectionItem.Items.Add(new Separator());

            var newColItem = new MenuItem { Header = "✚  New Collection…" };
            newColItem.Click += (_, _) =>
            {
                var dialog = new NewCollectionDialog { Owner = this };
                if (dialog.ShowDialog() != true) return;
                int newId = _db.CreateCollection(dialog.CollectionName);
                _db.AddGameToCollection(game.Id, newId);
                RefreshCollectionsSidebar();
            };
            collectionItem.Items.Add(newColItem);
            menu.Items.Add(collectionItem);

            menu.Items.Add(new Separator());

            // ── Rename Game ──
            menu.Items.Add(MakeMenuItem("✏  Rename Game", () =>
            {
                var rename = new RenameWindow(game.Title) { Owner = this };
                if (rename.ShowDialog() == true)
                {
                    game.Title = rename.NewTitle;
                    _db.UpdateTitle(game.Id, rename.NewTitle);
                    _vm.RefreshGame(game);
                }
            }));

            // ── Select All / bulk delete (flat view only) ──
            int selectedCount = GameGridView.SelectedItems.Count;
            if (GameGridView.Visibility == Visibility.Visible)
            {
                menu.Items.Add(new Separator());

                menu.Items.Add(MakeMenuItem("☑  Select All", () =>
                {
                    GameGridView.SelectAll();
                }));

                if (selectedCount > 1)
                {
                    var toDelete = GameGridView.SelectedItems.OfType<Game>().ToList();
                    var bulkDeleteItem = MakeMenuItem($"🗑  Delete Selected ({toDelete.Count})", async () =>
                        await DeleteGamesWithConfirmAsync(toDelete));
                    bulkDeleteItem.Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                        .ConvertFromString("#FF5F57")!);
                    menu.Items.Add(bulkDeleteItem);
                }
            }

            menu.Items.Add(new Separator());

            // ── Rename ──
            menu.Items.Add(MakeMenuItem("✎  Rename", () =>
            {
                var dialog = new Views.RenameWindow(game.Title) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    game.Title = dialog.NewTitle;
                    _db.UpdateTitle(game.Id, game.Title);
                    _vm.RefreshGame(game);
                }
            }));

            // ── Delete Game ──
            var deleteItem = MakeMenuItem("🗑  Delete Game", async () =>
            {
                var dlg = new Views.ConfirmDialog(
                    "Delete Game",
                    $"Remove \"{game.Title}\" from your library?\n\nThis will not delete the ROM file from your computer.",
                    confirmLabel: "Delete") { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    _db.DeleteGame(game.Id);
                    _vm.RemoveGame(game);
                    await _vm.FilterGamesAsync();
                }
            });

            deleteItem.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString("#FF5F57")!);

            menu.Items.Add(deleteItem);

            return menu;
        }

        private MenuItem MakeMenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => onClick();
            return item;
        }

        private MenuItem MakeMenuItem(string header, Func<Task> onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (s, e) => await onClick();
            return item;
        }

        // ── Keyboard shortcuts ──
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Enter — open detail card for focused game
            if (e.Key == Key.Enter &&
                GameGridView.Visibility == Visibility.Visible &&
                GameGridView.SelectedItem is Game focusedGame)
            {
                e.Handled = true;
                GameGridView.SelectedItems.Clear();
                _selectionAnchor = focusedGame;
                _openDetailWindow?.Close();
                _openDetailWindow = new GameDetailWindow(focusedGame) { Owner = this };
                _openDetailWindow.Closed += async (_, _) =>
                {
                    _openDetailWindow = null;
                    if (!_db.GameExists(focusedGame.Id))
                    {
                        _vm.RemoveGame(focusedGame);
                        await _vm.FilterGamesAsync();
                    }
                };
                _openDetailWindow.Show();
                return;
            }

            // Ctrl+A — select all
            if (e.Key == Key.A &&
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                GameGridView.Visibility == Visibility.Visible)
            {
                GameGridView.SelectAll();
                e.Handled = true;
                return;
            }

            // Delete — delete selected games
            if (e.Key == Key.Delete &&
                GameGridView.Visibility == Visibility.Visible &&
                GameGridView.SelectedItems.Count > 0)
            {
                e.Handled = true;
                var toDelete = GameGridView.SelectedItems.OfType<Game>().ToList();
                _ = DeleteGamesWithConfirmAsync(toDelete);
            }

            // Delete — delete selected screenshots
            if (e.Key == Key.Delete &&
                ScreenshotsView.Visibility == Visibility.Visible &&
                _selectedScreenshots.Count > 0)
            {
                e.Handled = true;
                DeleteScreenshotsWithConfirm(_selectedScreenshots.ToList());
            }
        }

        private async Task ReloadAndFilterAsync()
        {
            await Task.Run(() => _vm.Reload());
            await _vm.FilterGamesAsync();
            _vm.ToolbarTitle = _vm.SelectedConsole;
        }

        private async Task DeleteGamesWithConfirmAsync(List<Game> toDelete)
        {
            string msg = toDelete.Count == 1
                ? $"Delete \"{toDelete[0].Title}\"? Save states will not be removed."
                : $"Delete {toDelete.Count} games? Save states will not be removed.";

            var confirm = new Views.ConfirmDialog(
                toDelete.Count == 1 ? "Delete Game" : "Delete Games", msg)
                { Owner = this };
            if (confirm.ShowDialog() != true) return;

            await Task.Run(() => _db.DeleteGames(toDelete.Select(g => g.Id)));
            foreach (var g in toDelete) _vm.RemoveGame(g);
            GameGridView.SelectedItems.Clear();
            _selectionAnchor = null;

            // Rebuild the view so grouped headers and counts refresh immediately.
            await _vm.FilterGamesAsync();
        }

        // ── Tab switching ──
        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton btn && btn.Tag is string tag)
            {
                GameContentGrid.Visibility = tag == "Library"     ? Visibility.Visible : Visibility.Collapsed;
                SaveStatesView.Visibility  = tag == "SaveStates"  ? Visibility.Visible : Visibility.Collapsed;
                ScreenshotsView.Visibility = tag == "Screenshots" ? Visibility.Visible : Visibility.Collapsed;

                if (tag == "SaveStates")  PopulateSaveStatesView();
                if (tag == "Screenshots") PopulateScreenshotsView();

                UpdateTabStyles(tag);
            }
        }

        private void UpdateTabStyles(string activeTag)
        {
            TabLibrary.IsChecked     = activeTag == "Library";
            TabSaveStates.IsChecked  = activeTag == "SaveStates";
            TabScreenshots.IsChecked = activeTag == "Screenshots";
        }

        private void PopulateSaveStatesView()
        {
            SaveStatesPanel.Children.Clear();
            var allStates = _db.GetAllSaveStates();

            if (allStates.Count == 0)
            {
                SaveStatesEmptyText.Visibility = Visibility.Visible;
                return;
            }
            SaveStatesEmptyText.Visibility = Visibility.Collapsed;

            // Group per game (OpenEmu pattern). Key on RomHash when present so
            // states for the same physical ROM collapse into one section even if
            // the underlying GameId got reassigned (re-import, DB rebuild) or
            // the stored GameTitle drifted. Falls back to a normalized
            // title+console pair for legacy states with no hash.
            static string GroupKey(Models.SaveState s) =>
                !string.IsNullOrEmpty(s.RomHash)
                    ? "hash:" + s.RomHash.ToLowerInvariant()
                    : "title:" + (s.GameTitle ?? "").Trim().ToLowerInvariant()
                        + "|" + (s.ConsoleName ?? "").Trim().ToLowerInvariant();

            var grouped = allStates
                .GroupBy(GroupKey)
                .Select(g => new
                {
                    Title   = g.Select(x => x.GameTitle).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "",
                    Console = g.Select(x => x.ConsoleName).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "",
                    States  = g.OrderByDescending(x => x.CreatedAt).ToList(),
                })
                .OrderBy(g => g.Title)
                .ThenBy(g => g.Console);

            foreach (var group in grouped)
            {
                SaveStatesPanel.Children.Add(BuildSaveStateGroupHeader(
                    string.IsNullOrEmpty(group.Title) ? "Deleted Game" : group.Title,
                    group.Console));

                // Card wrap panel for this game's states. Horizontal margin
                // matches the gap between sidebar/scrollbar and card area now
                // that the ScrollViewer's left/right padding was removed.
                var wrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(16, 8, 16, 0),
                };
                foreach (var s in group.States)
                    wrap.Children.Add(BuildSaveStateCard(s));
                SaveStatesPanel.Children.Add(wrap);
            }
        }

        // OpenEmu-style section header: full-width horizontal bar using the same
        // engraved gradient as the top toolbar, with the game name on the left
        // and the system name on the right (semibold left, secondary text right).
        private FrameworkElement BuildSaveStateGroupHeader(string gameTitle, string consoleName)
        {
            var border = new Border
            {
                Background      = (System.Windows.Media.Brush)FindResource("ToolbarRaisedFillBrush"),
                BorderBrush     = (System.Windows.Media.Brush)FindResource("ToolbarChiselBrush"),
                BorderThickness = new Thickness(0, 1, 0, 1),
                // The SaveStatesView ScrollViewer uses vertical-only padding so
                // this bar can run edge-to-edge from the sidebar to the
                // scrollbar without negative-margin tricks.
                Margin          = new Thickness(0, 16, 0, 0),
                Height          = 32,
            };
            var grid = new Grid { Margin = new Thickness(20, 0, 20, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Top inner highlight gives the same raised-edge feel as the toolbar buttons.
            var topInner = new Border
            {
                BorderBrush     = (System.Windows.Media.Brush)FindResource("ToolbarTopHighlightBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            var name = new TextBlock
            {
                Text                = gameTitle,
                FontFamily          = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize            = 13,
                FontWeight          = FontWeights.SemiBold,
                Foreground          = (System.Windows.Media.Brush)FindResource("ToolbarRaisedTextBrush"),
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                Effect              = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius  = 0,
                    Direction   = 270,
                    ShadowDepth = 1,
                    Opacity     = 0.85,
                    Color       = System.Windows.Media.Colors.Black,
                },
            };
            var system = new TextBlock
            {
                Text                = consoleName,
                FontFamily          = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize            = 11,
                Foreground          = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(system, 1);
            grid.Children.Add(name);
            grid.Children.Add(system);

            var stack = new Grid();
            stack.Children.Add(topInner);
            stack.Children.Add(grid);
            border.Child = stack;
            return border;
        }

        private void PopulateFavoritesView()
        {
            FavoritesPanel.Children.Clear();
            var favs = _db.GetFavorites();

            if (favs.Count == 0)
            {
                FavoritesPanel.Children.Add(new TextBlock
                {
                    Text = "No favorites yet. Right-click a game and choose Add to Favorites.",
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                    FontSize = 13,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 60, 0, 0),
                });
                return;
            }

            var grouped = favs.GroupBy(g => g.Console).OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                // Console header — same style as save states
                var header = new TextBlock
                {
                    Text       = group.Key.Length > 0 ? group.Key : "Unknown",
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                    FontSize   = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    Margin     = new Thickness(0, 16, 0, 8),
                };
                FavoritesPanel.Children.Add(header);

                var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var game in group.OrderBy(g => g.Title))
                {
                    // Reuse the same card dimensions as the library grid
                    var card = new Border
                    {
                        Width        = 148,
                        Margin       = new Thickness(0, 0, 12, 12),
                        CornerRadius = new CornerRadius(8),
                        ClipToBounds = true,
                        Cursor       = Cursors.Hand,
                        Background   = System.Windows.Media.Brushes.Transparent,
                        DataContext  = game,
                    };

                    var artBorder = new Border
                    {
                        Height       = 200,
                        ClipToBounds = true,
                        Background   = System.Windows.Media.Brushes.Transparent,
                    };

                    string? artPath = game.DisplayArtPath;
                    if (!string.IsNullOrEmpty(artPath) && File.Exists(artPath))
                    {
                        try
                        {
                            var img = new System.Windows.Controls.Image
                            {
                                Source  = new System.Windows.Media.Imaging.BitmapImage(new Uri(artPath)),
                                Stretch = System.Windows.Media.Stretch.Uniform,
                            };
                            artBorder.Child = img;
                        }
                        catch { }
                    }
                    else
                    {
                        artBorder.Child = new TextBlock
                        {
                            Text              = game.Title,
                            FontFamily        = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                            FontSize          = 13,
                            FontWeight        = FontWeights.SemiBold,
                            Foreground        = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                            TextWrapping      = TextWrapping.Wrap,
                            TextAlignment     = TextAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin            = new Thickness(12),
                        };
                    }

                    card.Child = artBorder;
                    card.MouseLeftButtonDown += (_, e) => GameCard_Click(card, e);
                    card.MouseRightButtonUp  += (_, e) => GameCard_RightClick(card, e);

                    wrap.Children.Add(card);
                }
                FavoritesPanel.Children.Add(wrap);
            }
        }

        private void PopulateScreenshotsView()
        {
            ScreenshotsPanel.Children.Clear();
            _selectedScreenshots.Clear();

            var service     = new Services.ScreenshotService();
            var screenshots = service.GetAll();

            if (screenshots.Count == 0)
            {
                ScreenshotsEmptyState.Visibility = Visibility.Visible;
                return;
            }
            ScreenshotsEmptyState.Visibility = Visibility.Collapsed;

            // Group per game, same shape as PopulateSaveStatesView. The Screenshot
            // model doesn't carry a RomHash, so group by normalized title+console.
            // Each group renders as: header bar (BuildSaveStateGroupHeader — title
            // left, console right, raised-edge divider) followed by a WrapPanel of
            // the game's screenshot cards, newest first.
            static string GroupKey(Models.Screenshot s) =>
                (s.GameTitle ?? "").Trim().ToLowerInvariant()
                    + "|" + (s.Console ?? "").Trim().ToLowerInvariant();

            var grouped = screenshots
                .GroupBy(GroupKey)
                .Select(g => new
                {
                    Title   = g.Select(x => x.GameTitle).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "",
                    Console = g.Select(x => x.Console).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "",
                    Items   = g.OrderByDescending(x => x.TakenAt).ToList(),
                })
                .OrderBy(g => g.Title)
                .ThenBy(g => g.Console);

            foreach (var group in grouped)
            {
                ScreenshotsPanel.Children.Add(BuildSaveStateGroupHeader(
                    string.IsNullOrEmpty(group.Title) ? "Deleted Game" : group.Title,
                    group.Console));

                var wrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(16, 8, 16, 0),
                };
                foreach (var ss in group.Items)
                    wrap.Children.Add(BuildScreenshotCard(ss));
                ScreenshotsPanel.Children.Add(wrap);
            }
        }

        private FrameworkElement BuildScreenshotCard(Models.Screenshot ss)
        {
            var selectedBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E03535")!);
            var normalBrush = System.Windows.Media.Brushes.Transparent;

            var card = new Border
            {
                Width        = 240,
                Margin       = new Thickness(0, 0, 12, 12),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = false,
                Cursor       = Cursors.Hand,
                BorderThickness = new Thickness(2),
                BorderBrush  = normalBrush,
            };

            var innerBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background   = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1F1F21")!),
            };

            var stack = new StackPanel();

            // Console label
            stack.Children.Add(new TextBlock
            {
                Text       = ss.Console,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                Margin     = new Thickness(8, 6, 8, 4),
            });

            // Screenshot image
            var imgBorder = new Border { Height = 135, ClipToBounds = true, Background = System.Windows.Media.Brushes.Black };
            if (System.IO.File.Exists(ss.FilePath))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource        = new Uri(ss.FilePath, UriKind.Absolute);
                    bmp.CacheOption      = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 240;
                    bmp.EndInit();
                    bmp.Freeze();
                    imgBorder.Child = new System.Windows.Controls.Image { Source = bmp, Stretch = System.Windows.Media.Stretch.UniformToFill };
                }
                catch { /* leave black */ }
            }
            stack.Children.Add(imgBorder);

            stack.Children.Add(new TextBlock
            {
                Text         = ss.GameTitle,
                FontFamily   = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize     = 12,
                FontWeight   = FontWeights.SemiBold,
                Foreground   = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                Margin       = new Thickness(8, 6, 8, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            stack.Children.Add(new TextBlock
            {
                Text       = ss.TakenAtDisplay,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize   = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                Margin     = new Thickness(8, 0, 8, 8),
            });

            innerBorder.Child = stack;
            card.Child        = innerBorder;

            // Shift+click → toggle selection
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    if (_selectedScreenshots.Contains(ss.FilePath))
                    {
                        _selectedScreenshots.Remove(ss.FilePath);
                        card.BorderBrush = normalBrush;
                    }
                    else
                    {
                        _selectedScreenshots.Add(ss.FilePath);
                        card.BorderBrush = selectedBrush;
                    }
                    e.Handled = true;
                }
                else
                {
                    // Normal click — open full-size
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ss.FilePath) { UseShellExecute = true }); }
                    catch { }
                }
            };

            // Right-click → context menu
            card.MouseRightButtonUp += (_, e) =>
            {
                var paths = _selectedScreenshots.Count > 0
                    ? _selectedScreenshots.ToList()
                    : new List<string> { ss.FilePath };

                string label = paths.Count == 1
                    ? "🗑  Delete Screenshot"
                    : $"🗑  Delete {paths.Count} Screenshots";

                var menu = new ContextMenu();
                menu.Items.Add(MakeMenuItem(label, () => DeleteScreenshotsWithConfirm(paths)));
                menu.IsOpen = true;
                e.Handled   = true;
            };

            return card;
        }

        private void DeleteScreenshotsWithConfirm(List<string> paths)
        {
            string msg = paths.Count == 1
                ? "Delete this screenshot?"
                : $"Delete {paths.Count} screenshots?";

            var confirm = new Views.ConfirmDialog("Delete Screenshots", msg) { Owner = this };
            if (confirm.ShowDialog() != true) return;

            foreach (string path in paths)
            {
                try { System.IO.File.Delete(path); } catch { }
            }

            _selectedScreenshots.Clear();
            PopulateScreenshotsView();
        }

        private FrameworkElement BuildSaveStateCard(Models.SaveState s)
        {
            var card = new Border
            {
                Width         = 148,
                Margin        = new Thickness(0, 0, 12, 12),
                CornerRadius  = new CornerRadius(8),
                ClipToBounds  = true,
                Cursor        = Cursors.Hand,
                Background    = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1F1F21")!),
            };

            var stack = new StackPanel();

            // Screenshot thumbnail
            var thumb = new Border { Height = 100, ClipToBounds = true, Background = System.Windows.Media.Brushes.Black };
            if (s.ScreenshotPath.Length > 0 && File.Exists(s.ScreenshotPath))
            {
                try
                {
                    var img = new System.Windows.Controls.Image
                    {
                        Source  = new System.Windows.Media.Imaging.BitmapImage(new Uri(s.ScreenshotPath)),
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                    };
                    thumb.Child = img;
                }
                catch { }
            }
            stack.Children.Add(thumb);

            // Info area
            var info = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
            info.Children.Add(new TextBlock
            {
                Text         = s.Name,
                FontFamily   = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize     = 11,
                FontWeight   = FontWeights.SemiBold,
                Foreground   = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text       = s.GameTitle,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize   = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                Margin     = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text       = s.RelativeTime,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize   = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                Margin     = new Thickness(0, 2, 0, 0),
            });
            stack.Children.Add(info);

            card.Child = stack;

            // Hover highlight
            card.MouseEnter += (_, _) => card.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2A2A2D")!);
            card.MouseLeave += (_, _) => card.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1F1F21")!);

            // Right-click context menu
            card.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                BuildSaveStateContextMenu(s).IsOpen = true;
            };

            // Left-click = load (launch game with this state)
            card.MouseLeftButtonUp += (_, _) => LaunchWithSaveState(s);

            return card;
        }

        private ContextMenu BuildSaveStateContextMenu(Models.SaveState s)
        {
            var menu = new ContextMenu();

            menu.Items.Add(MakeMenuItem("▶  Load State", () => LaunchWithSaveState(s)));

            menu.Items.Add(MakeMenuItem("✏  Rename", () =>
            {
                var rename = new RenameWindow(s.Name) { Owner = this };
                if (rename.ShowDialog() != true) return;

                string newName  = rename.NewTitle;
                string safeName = new string(newName.Select(c =>
                    Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();

                string dir       = Path.GetDirectoryName(s.StatePath) ?? "";
                string newState  = Path.Combine(dir, safeName + ".state");
                string newPng    = Path.Combine(dir, safeName + ".png");
                string newJson   = Path.Combine(dir, safeName + ".json");
                string oldJson   = Path.ChangeExtension(s.StatePath, ".json");

                try
                {
                    if (File.Exists(s.StatePath))  File.Move(s.StatePath,  newState, overwrite: true);
                    if (File.Exists(s.ScreenshotPath)) File.Move(s.ScreenshotPath, newPng, overwrite: true);
                    if (File.Exists(oldJson))       File.Move(oldJson, newJson, overwrite: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Rename failed: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _db.UpdateSaveStateName(s.Id, newName, newState, newPng);
                PopulateSaveStatesView();
            }));

            menu.Items.Add(new Separator());

            var delItem = MakeMenuItem("🗑  Delete", () =>
            {
                var dlg = new ConfirmDialog(
                    "Delete Save State",
                    $"Delete \"{s.Name}\"? This cannot be undone.")
                { Owner = this };
                if (dlg.ShowDialog() != true) return;

                // Delete all sidecars next to the .state. Derive the .png and
                // .json paths from the .state filename so we still clean up
                // orphaned files even if the DB row's ScreenshotPath is empty
                // (capture failed at save time, legacy rows, etc.).
                try { if (File.Exists(s.StatePath))      File.Delete(s.StatePath);      } catch { }
                try { if (File.Exists(s.ScreenshotPath)) File.Delete(s.ScreenshotPath); } catch { }
                try
                {
                    string p = Path.ChangeExtension(s.StatePath, ".png");
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { }
                try
                {
                    string j = Path.ChangeExtension(s.StatePath, ".json");
                    if (File.Exists(j)) File.Delete(j);
                }
                catch { }

                _db.DeleteSaveState(s.Id);
                PopulateSaveStatesView();
            });
            delItem.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF5F57")!);
            menu.Items.Add(delItem);

            return menu;
        }

        private void LaunchWithSaveState(Models.SaveState s)
        {
            var game = _db.GetGameById(s.GameId);
            if (game == null)
            {
                MessageBox.Show("Game not found in library.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string? corePath = _coreManager.GetCorePath(game.Console);
            if (corePath == null)
            {
                MessageBox.Show($"No core found for {game.Console}.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // Match the launch sequence used by GameDetailWindows: free the
                // previous run's core DLL BEFORE LoadLibrary so the refcount
                // actually reaches zero and the DLL globals reset. Without this
                // the second N64 launch in a session crashes ("Failed to
                // initialize core") because mupen64plus-next leaves stale
                // Vulkan state behind that breaks retro_init on relaunch.
                Views.EmulatorWindow.FreeStaleDll();
                var core = new Services.LibretroCore(corePath);
                var emu  = new Views.EmulatorWindow(game, core, s.StatePath) { Owner = this };
                emu.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Sort ──
        private void SortGames_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && _vm != null)
            {
                var tag = (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                var sorted = tag switch
                {
                    "year" => new ObservableCollection<Game>(
                                    _vm.Games.OrderByDescending(g => g.Year)),
                    "played" => new ObservableCollection<Game>(
                                    _vm.Games.OrderByDescending(g => g.LastPlayed)),
                    "rating" => new ObservableCollection<Game>(
                                    _vm.Games.OrderByDescending(g => g.Rating)),
                    _ => new ObservableCollection<Game>(
                                    _vm.Games.OrderBy(g => g.Title)),
                };
                _vm.Games = sorted;
            }
        }
    }
}