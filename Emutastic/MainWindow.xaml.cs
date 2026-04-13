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
            _artworkFetch = new ArtworkFetchService(_db, _artwork, _vm);
            _vm.Navigated += OnNavigated;
            _artworkFetch.BoxArt3DFetched += () =>
                Dispatcher.Invoke(() => BoxArtTogglePanel.Visibility = Visibility.Visible);
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

            _ = _artworkFetch.RetryMissingArtworkAsync();
            _ = _artworkFetch.BackfillMetadataAsync();

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
            if (theme == null || string.IsNullOrWhiteSpace(theme.BackgroundImagePath)
                || !System.IO.File.Exists(theme.BackgroundImagePath))
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
                bmp.UriSource = new Uri(theme.BackgroundImagePath, UriKind.Absolute);
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
                    _importer.ImportFilesAsync(romFiles);
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

            // Toggle between favorites grouped view and normal library grid
            if (tag == "Favorites")
            {
                FavoritesGroupedView.Visibility = Visibility.Visible;
                GameGridView.Visibility = Visibility.Collapsed;
                LibraryView.Visibility = Visibility.Collapsed;
                PopulateFavoritesView();
            }
            else
            {
                FavoritesGroupedView.Visibility = Visibility.Collapsed;
                // Re-apply the IsGroupedView bindings that we broke by setting Visibility directly
                GameGridView.SetBinding(VisibilityProperty,
                    new System.Windows.Data.Binding("IsGroupedView")
                    { Converter = (System.Windows.Data.IValueConverter)FindResource("InverseBoolToVisibility") });
                LibraryView.SetBinding(VisibilityProperty,
                    new System.Windows.Data.Binding("IsGroupedView")
                    { Converter = (System.Windows.Data.IValueConverter)FindResource("BoolToVisibility") });
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
                _importer.ImportFilesAsync(dialog.FileNames);
            }
        }

        private void SetStatus(string msg, bool autoClear = false)
            => _vm.SetStatus(msg, autoClear);

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
                if (_vm.IsShowingFavorites)
                {
                    // Favorites uses its own grouped panel, not the binding-driven views
                    FavoritesGroupedView.Visibility = Visibility.Visible;
                    GameGridView.Visibility = Visibility.Collapsed;
                    LibraryView.Visibility = Visibility.Collapsed;
                }
                else
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
            }
            else
            {
                GameGridView.Visibility = Visibility.Collapsed;
                LibraryView.Visibility  = Visibility.Collapsed;
                FavoritesGroupedView.Visibility = Visibility.Collapsed;
            }
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
                    if (use3D) Game.Consoles3D.Add(c);
                    else       Game.Consoles3D.Remove(c);
                }
            }
            else
            {
                string console = _vm.SelectedConsole ?? "";
                if (use3D) Game.Consoles3D.Add(console);
                else       Game.Consoles3D.Remove(console);
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