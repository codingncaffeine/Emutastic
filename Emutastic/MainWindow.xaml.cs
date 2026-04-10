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
        private ControllerManager? _controllerManager;
        private CoreManager _coreManager = null!;
        private Button? _selectedNavButton;
        private Game?   _selectionAnchor;   // anchor for Shift+click range selection
        private readonly HashSet<string> _selectedScreenshots = new(); // selected file paths
        private System.Windows.Threading.DispatcherTimer? _dragLeaveTimer;
        private GameDetailWindow? _openDetailWindow;
        private bool _isShowingFavorites;

        public MainWindow()
        {
            InitializeComponent();
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/emutastic-logo.ico"));
            ApplyWindowsChrome();
            AllowDrop = true;

            // Everything else deferred to Loaded so the window appears immediately.
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded; // fire once

            // ── Phase 1: synchronous, fast — window becomes interactive immediately ──
            _db          = new DatabaseService();   // schema init (CREATE TABLE / indexes)
            _artwork     = new ArtworkService();
            _coreManager = new CoreManager(App.Configuration!);
            _importer    = new ImportService(_db, _coreManager, App.Configuration);
            _vm          = new MainViewModel(_db);  // empty _allGames until Reload() runs
            DataContext  = _vm;                     // _vm is now non-null; clicks work

            _importer.StatusChanged += msg =>
                Dispatcher.Invoke(() => SetStatus(msg));

            _importer.ProgressChanged += (current, total) =>
                Dispatcher.Invoke(() =>
                {
                    if (total == 0) return;
                    if (current >= total)
                    {
                        SetStatus("Import complete", autoClear: true);
                        return;
                    }
                    int pct = (int)((current / (double)total) * 100);
                    SetStatus($"Importing… {pct}%  ({current} of {total})");
                });
            _importer.GameImported += game =>
                Dispatcher.Invoke(() => _vm.RefreshGame(game));
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

            // ── Phase 2: load data off UI thread, then filter on UI thread ──
            await Task.Run(() => _vm.Reload());  // GetAllGames() — stays off UI thread
            await _vm.FilterGamesAsync();        // sort/group in background, assign on UI thread
            ScrollLibraryToTop();

            UpdateBoxArtToggleVisibility();

            _ = RetryMissingArtworkAsync();
        }

        private void InitializeControllerManager()
        {
            if (_controllerManager == null && App.Configuration != null)
            {
                _controllerManager = new ControllerManager(App.Configuration);
            }
        }

        // ── Artwork retry on startup ──
        // Runs after the grid is already visible. Capped at 25 per session so it
        // doesn't block the UI thread or hammer the server on large libraries.
        private async Task RetryMissingArtworkAsync()
        {
            var missing = await Task.Run(() => _db.GetGamesWithoutArtwork());
            if (missing.Count == 0) return;

            // Repair pass: fix games whose artwork file is already on disk but the DB
            // path was never saved (e.g. background task killed on last shutdown).
            // This is instant — no HTTP requests — and removes the bulk of false positives.
            var stillMissing = new List<Models.Game>();
            await Task.Run(() =>
            {
                foreach (var game in missing)
                {
                    string? cached = _artwork.FindCachedArtwork(game.RomHash, game.Console);
                    if (cached != null)
                    {
                        _db.UpdateCoverArt(game.Id, cached);
                        game.CoverArtPath = cached;
                        Dispatcher.Invoke(() => _vm.RefreshGame(game));
                    }
                    else
                    {
                        stillMissing.Add(game);
                    }
                }
            });

            if (stillMissing.Count == 0) return;
            await FetchArtworkForGamesAsync(stillMissing, "Artwork", silentThreshold: 1);
        }

        private async Task FetchMissingArtworkForConsoleAsync(string console, string displayName)
        {
            var missing = await Task.Run(() => _db.GetGamesWithoutArtworkForConsole(console));
            if (missing.Count == 0)
            {
                SetStatus($"{displayName} — all artwork already downloaded", autoClear: true);
                return;
            }
            await FetchArtworkForGamesAsync(missing, displayName);
        }

        private async Task Fetch3DBoxArtForConsoleAsync(string console, string displayName)
        {
            var snapConfig = App.Configuration?.GetSnapConfiguration();
            if (snapConfig == null || !snapConfig.ScreenScraperEnabled
                || string.IsNullOrWhiteSpace(snapConfig.ScreenScraperUser))
            {
                SetStatus("ScreenScraper not configured — set up in Preferences → Snaps", autoClear: true);
                return;
            }

            var games = await Task.Run(() => _db.GetGamesWithout3DBoxArtForConsole(console));
            if (games.Count == 0)
            {
                SetStatus($"{displayName} — all 3D box art already downloaded", autoClear: true);
                return;
            }

            int total = games.Count;
            int done = 0;
            int fetched = 0;
            int overQuota = 0;

            int ssThreads = Math.Max(1, snapConfig.ScreenScraperMaxThreads);
            SetStatus($"{displayName} — downloading 3D box art for {total} games…");

            // Each worker gets its own ScreenScraperService (own HttpClient / TCP connection)
            // so ScreenScraper sees N distinct concurrent connections, not 1 multiplexed connection.
            var workers = new System.Collections.Concurrent.ConcurrentQueue<ScreenScraperService>();
            for (int i = 0; i < ssThreads; i++)
                workers.Enqueue(new ScreenScraperService());
            var sem = new System.Threading.SemaphoreSlim(ssThreads, ssThreads);

            var tasks = games.Select(game => Task.Run(async () =>
            {
                if (System.Threading.Interlocked.CompareExchange(ref overQuota, 0, 0) != 0)
                    return;

                await sem.WaitAsync();
                ScreenScraperService worker;
                while (!workers.TryDequeue(out worker!))
                    await Task.Delay(10);
                try
                {
                    if (System.Threading.Interlocked.CompareExchange(ref overQuota, 0, 0) != 0)
                        return;

                    var result = await worker.FetchBoxArt3DAsync(
                        snapConfig.ScreenScraperUser, snapConfig.ScreenScraperPassword,
                        game.Console, game.RomHash, game.RomPath);

                    if (result.OverQuota)
                    {
                        System.Threading.Interlocked.Exchange(ref overQuota, 1);
                        Dispatcher.Invoke(() =>
                            SetStatus($"{displayName} — ScreenScraper daily limit reached ({fetched} downloaded)", autoClear: true));
                        return;
                    }

                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                        System.Diagnostics.Debug.WriteLine($"[3D BoxArt] {game.Title}: {result.ErrorMessage}");

                    if (result.LocalPath != null)
                    {
                        _db.UpdateBoxArt3D(game.Id, result.LocalPath);
                        game.BoxArt3DPath = result.LocalPath;
                        System.Threading.Interlocked.Increment(ref fetched);
                        Dispatcher.Invoke(() => _vm.RefreshGame(game));
                    }

                    int completed = System.Threading.Interlocked.Increment(ref done);
                    int pct = (int)((completed / (double)total) * 100);
                    Dispatcher.Invoke(() =>
                        SetStatus($"{displayName} 3D Box Art — {pct}%  ({completed} of {total})  {game.Title}"));
                }
                finally
                {
                    workers.Enqueue(worker);
                    sem.Release();
                }
            })).ToList();

            await Task.WhenAll(tasks);

            SetStatus(fetched > 0
                ? $"{displayName} — {fetched} 3D box art image{(fetched == 1 ? "" : "s")} downloaded"
                : $"{displayName} — no 3D box art found on ScreenScraper", autoClear: true);

            // Show the 2D/3D toggle if we got any
            if (fetched > 0)
                Dispatcher.Invoke(() => BoxArtTogglePanel.Visibility = Visibility.Visible);
        }

        // silentThreshold: games with ArtworkAttempts >= this value produce no status messages.
        // Pass int.MaxValue (default) to always show progress — used for manual fetches.
        // Pass 2 for the auto startup retry so repeat failures are completely silent.
        private async Task FetchArtworkForGamesAsync(List<Models.Game> games, string label,
            int silentThreshold = int.MaxValue)
        {
            // Separate into "loud" (new / few attempts) and "silent" (repeated failures) groups.
            var loudGames   = games.Where(g => g.ArtworkAttempts < silentThreshold).ToList();
            var silentGames = games.Where(g => g.ArtworkAttempts >= silentThreshold).ToList();

            int total   = loudGames.Count;
            int done    = 0;
            int fetched = 0;
            var sem     = new System.Threading.SemaphoreSlim(6, 6);

            if (total > 0)
                SetStatus($"{label} — starting artwork fetch for {total} games…");

            // Process loud games with status updates.
            var loudTasks = loudGames.Select(async game =>
            {
                await sem.WaitAsync();
                try
                {
                    var (artworkPath, metadata) = await _artwork.FetchArtworkAsync(
                        game.RomHash, game.RomPath, game.Console);

                    if (artworkPath != null)
                    {
                        _db.UpdateCoverArt(game.Id, artworkPath);
                        game.CoverArtPath = artworkPath;
                        System.Threading.Interlocked.Increment(ref fetched);

                        if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                        {
                            game.Title = metadata.Title;
                            _db.UpdateTitle(game.Id, metadata.Title);
                        }

                        Dispatcher.Invoke(() => _vm.RefreshGame(game));
                    }
                    else
                    {
                        _db.IncrementArtworkAttempts(game.Id);
                    }

                    int completed = System.Threading.Interlocked.Increment(ref done);
                    int pct = (int)((completed / (double)total) * 100);
                    Dispatcher.Invoke(() =>
                        SetStatus($"{label} — {pct}%  ({completed} of {total})  {game.Title}"));
                }
                finally { sem.Release(); }
            });

            await Task.WhenAll(loudTasks);

            if (total > 0)
            {
                SetStatus(fetched > 0
                    ? $"{label} — {fetched} image{(fetched == 1 ? "" : "s")} downloaded"
                    : $"{label} — no artwork found", autoClear: true);
            }

            // Process silent games in the background with no status output.
            var silentTasks = silentGames.Select(async game =>
            {
                await sem.WaitAsync();
                try
                {
                    var (artworkPath, metadata) = await _artwork.FetchArtworkAsync(
                        game.RomHash, game.RomPath, game.Console);

                    if (artworkPath != null)
                    {
                        _db.UpdateCoverArt(game.Id, artworkPath);
                        game.CoverArtPath = artworkPath;

                        if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                        {
                            game.Title = metadata.Title;
                            _db.UpdateTitle(game.Id, metadata.Title);
                        }

                        Dispatcher.Invoke(() => _vm.RefreshGame(game));
                    }
                    else
                    {
                        _db.IncrementArtworkAttempts(game.Id);
                    }
                }
                finally { sem.Release(); }
            });

            await Task.WhenAll(silentTasks);
        }

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
        /// Called after navigation so the view always starts at the first item.
        /// </summary>
        private void ScrollLibraryToTop()
        {
            // GameGridView (flat grid) — ListBox has an internal ScrollViewer
            var gridSv = FindVisualChild<ScrollViewer>(GameGridView);
            gridSv?.ScrollToTop();

            // LibraryView (grouped scroll viewer)
            LibraryView?.ScrollToTop();

            // GameListView (list mode) — ListBox has an internal ScrollViewer
            var listSv = FindVisualChild<ScrollViewer>(GameListView);
            listSv?.ScrollToTop();
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

        protected override async void OnDrop(DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                await _importer.ImportFilesAsync(files);
                await Task.Run(() => _vm.Reload());
                await _vm.FilterGamesAsync();
                UpdateToolbarTitle(_vm.SelectedConsole);
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
            _isShowingFavorites = false; // cleared here; NavFavorites_Click sets it back to true
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
            {
                _vm.SelectedConsole = console;
                await _vm.FilterGamesAsync();
                ScrollLibraryToTop();
                UpdateToolbarTitle(console);
                // Find and highlight the matching sidebar button if present
                foreach (var child in SidebarPanel.Children.OfType<Button>())
                {
                    if (child.Tag is string t && t == console)
                    {
                        SelectNavButton(child);
                        break;
                    }
                }
            }
        }

        private async void NavAllGames_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton((Button)sender);
            _vm.SelectedConsole = "All Games";
            await _vm.FilterGamesAsync();
            ScrollLibraryToTop();
            UpdateToolbarTitle("All Games");
        }

        private void NavRecent_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton((Button)sender);
            _vm.LoadRecent(_db);
            ScrollLibraryToTop();
            UpdateToolbarTitle("Recently Played");
        }

        private void NavFavorites_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton((Button)sender);
            _isShowingFavorites = true;
            _vm.LoadFavorites(_db);
            ScrollLibraryToTop();
            UpdateToolbarTitle("Favorites");
        }

        private async void NavConsole_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SelectNavButton(btn);
                _vm.SelectedConsole = tag;
                await _vm.FilterGamesAsync();
                ScrollLibraryToTop();
                UpdateBoxArtToggleVisibility();
                ShowNavCount(btn, _vm.Games.Count);
                string name = btn.Content is StackPanel sp
                    ? sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? tag
                    : tag;
                UpdateToolbarTitle(name);
            }
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

                if (source is Button consoleBtn && consoleBtn.Tag is string console && !string.IsNullOrEmpty(console))
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
                    if (count == 0) return;

                    var menu = new ContextMenu();
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
                        _ = Task.Run(() => _vm.Reload()).ContinueWith(_ =>
                            Dispatcher.Invoke(async () =>
                            {
                                await _vm.FilterGamesAsync();
                                UpdateToolbarTitle(_vm.SelectedConsole);
                            }));
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

        private void NavRecentlyAdded_Click(object sender, RoutedEventArgs e)
        {
            SelectNavButton((Button)sender);
            var games = _db.GetRecentlyAdded(25);
            _vm.Games = new ObservableCollection<Game>(games);
            _vm.IsGroupedView = false;
            _vm.GameCountText = $"{games.Count} games";
            UpdateToolbarTitle("Recently Added");
        }

        private void NavUserCollection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int collectionId) return;
            SelectNavButton(btn);
            var games = _db.GetGamesByCollectionId(collectionId);
            _vm.Games = new ObservableCollection<Game>(games);
            _vm.IsGroupedView = false;
            _vm.GameCountText = $"{games.Count} games";
            string displayName = btn.Content?.ToString()?.Replace("📂  ", "") ?? "Collection";
            UpdateToolbarTitle(displayName);
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

        private void NavPreferences_Click(object sender, RoutedEventArgs e) 
        { 
            InitializeControllerManager();
            var prefs = new PreferencesWindow(_db, _controllerManager!, App.Configuration!) { Owner = this };
            prefs.ShowDialog();
        }

        private async void NavImport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Import ROMs",
                Filter = "ROM Files|*.nes;*.sfc;*.smc;*.z64;*.n64;*.gb;*.gbc;*.gba;*.nds;*.md;*.gen;*.sms;*.gg;*.pce;*.iso;*.pbp;*.cso;*.a26;*.a52;*.a78;*.lnx;*.zip;*.7z|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                await _importer.ImportFilesAsync(dialog.FileNames);
                await Task.Run(() => _vm.Reload());
                await _vm.FilterGamesAsync();
                UpdateToolbarTitle(_vm.SelectedConsole);
            }
        }

        private void UpdateToolbarTitle(string title) => ToolbarTitle.Text = title;

        private CancellationTokenSource? _statusClearCts;
        private void SetStatus(string msg, bool autoClear = false)
        {
            _statusClearCts?.Cancel();
            StatusText.Text = msg;
            if (!autoClear) return;
            _statusClearCts = new CancellationTokenSource();
            var token = _statusClearCts.Token;
            _ = Task.Delay(3000, token).ContinueWith(_ =>
                Dispatcher.Invoke(() => StatusText.Text = ""), token,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }

        // ── View toggle (grid / list) ──
        private void ViewToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked) return;
            bool listActive = clicked.Tag?.ToString() == "List";
            ViewGrid.IsChecked = !listActive;
            ViewList.IsChecked = listActive;

            // List view: show list, hide both grid views.
            // Grid view: restore visibility to IsGroupedView binding state.
            GameListView.Visibility = listActive ? Visibility.Visible : Visibility.Collapsed;
            if (!listActive)
            {
                // Restore binding-driven visibility for the two grid views.
                GameGridView.SetBinding(VisibilityProperty,
                    new System.Windows.Data.Binding("IsGroupedView")
                    {
                        Converter = (System.Windows.Data.IValueConverter)FindResource("InverseBoolToVisibility")
                    });
                LibraryView.SetBinding(VisibilityProperty,
                    new System.Windows.Data.Binding("IsGroupedView")
                    {
                        Converter = (System.Windows.Data.IValueConverter)FindResource("BoolToVisibility")
                    });
            }
            else
            {
                GameGridView.Visibility = Visibility.Collapsed;
                LibraryView.Visibility  = Visibility.Collapsed;
            }
        }

        private void BoxArtToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked) return;
            bool use3D = clicked.Tag?.ToString() == "3D";
            BoxArt2D.IsChecked = !use3D;
            BoxArt3D.IsChecked = use3D;

            string console = _vm.SelectedConsole ?? "";
            if (use3D)
                Game.Consoles3D.Add(console);
            else
                Game.Consoles3D.Remove(console);

            // Persist preference
            var snapConfig = App.Configuration?.GetSnapConfiguration();
            if (snapConfig != null)
            {
                snapConfig.Use3DBoxArtConsoles = new System.Collections.Generic.List<string>(Game.Consoles3D);
                App.Configuration!.SetSnapConfiguration(snapConfig);
            }

            // Refresh only the current view
            _vm.RefreshAllGames();
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
                if (_isShowingFavorites)
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
                SetStatus($"Fetching artwork for {game.Title}…");
                var (artworkPath, metadata) = await _artwork.FetchArtworkAsync(
                    game.RomHash, game.RomPath, game.Console);

                if (artworkPath != null)
                {
                    _db.UpdateCoverArt(game.Id, artworkPath);
                    game.CoverArtPath = artworkPath;
                    _vm.RefreshGame(game);
                    SetStatus("Artwork updated", autoClear: true);
                }
                else
                {
                    SetStatus("No artwork found", autoClear: true);
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

            // Group by console
            var grouped = allStates.GroupBy(s => s.ConsoleName).OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                // Console header
                var header = new TextBlock
                {
                    Text       = group.Key.Length > 0 ? group.Key : "Unknown Console",
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                    FontSize   = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    Margin     = new Thickness(0, 16, 0, 8),
                };
                SaveStatesPanel.Children.Add(header);

                // Card wrap panel for this console
                var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

                foreach (var s in group.OrderByDescending(x => x.CreatedAt))
                {
                    var card = BuildSaveStateCard(s);
                    wrap.Children.Add(card);
                }
                SaveStatesPanel.Children.Add(wrap);
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

            foreach (var ss in screenshots)
                ScreenshotsPanel.Children.Add(BuildScreenshotCard(ss));
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

                try { if (File.Exists(s.StatePath))      File.Delete(s.StatePath);      } catch { }
                try { if (File.Exists(s.ScreenshotPath)) File.Delete(s.ScreenshotPath); } catch { }
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
                var core = new Services.LibretroCore(corePath);
                var emu  = new EmulatorWindow(game, core, s.StatePath) { Owner = this };
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