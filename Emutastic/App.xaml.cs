using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;
using Emutastic.Configuration;
using Emutastic.Services;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Debug;

namespace Emutastic
{
    public partial class App : Application
    {
        public static IConfigurationService? Configuration { get; private set; }
        public static ILogger? Logger { get; private set; }
        public static CoreOptionsService CoreOptions { get; private set; } = null!;

        /// <summary>True when first-run detected existing data at the chosen directory (no DB yet).</summary>
        public static bool FirstRunDiscoveryNeeded { get; set; }

        private static Mutex? _singleInstanceMutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Single-instance guard: if Emutastic is already running, bring it to
            // the front and exit this process instead of launching a second copy.
            _singleInstanceMutex = new Mutex(true, "Emutastic_SingleInstance_v1", out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // Find the existing window and activate it.
                var existing = System.Diagnostics.Process.GetProcessesByName(
                    System.Diagnostics.Process.GetCurrentProcess().ProcessName);
                foreach (var proc in existing)
                {
                    if (proc.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(proc.MainWindowHandle, 9); // SW_RESTORE
                        NativeMethods.SetForegroundWindow(proc.MainWindowHandle);
                    }
                }
                Shutdown();
                return;
            }

            // Trace.WriteLine (used throughout libretro callbacks AND the portable migration
            // helpers below) internally calls OutputDebugStringW, which raises SEH exception
            // 0x4001000a to signal a debugger.  When a debugger IS attached, the debugger
            // catches it silently.  When no debugger is attached (running outside VS), the
            // exception propagates through reverse P/Invoke boundaries on native threads
            // (e.g. mupen64plus EmuThread calling our env/log callbacks) and kills the process.
            //
            // Fix: when no debugger is attached, replace DefaultTraceListener
            // (OutputDebugString) with ConsoleTraceListener (writes to stderr, no SEH).
            // MUST run before the portable cores migration since that helper calls Trace.WriteLine.
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Trace.Listeners.Clear();
                System.Diagnostics.Trace.Listeners.Add(
                    new System.Diagnostics.ConsoleTraceListener(useErrorStream: true));
            }

            // Portable mode: must detect BEFORE config loads so the config service
            // routes to PortableData instead of %AppData%. Drop a portable.txt next
            // to the .exe to opt in.
            AppPaths.DetectPortableMode();

            // Portable mode v2 (v1.3.3): cores moved from [exe]/Cores/ → [DataRoot]/Cores/
            // so the entire portable experience sits inside PortableData/. Migrate any
            // pre-existing cores from the old location on first launch with the new code.
            MigratePortableCoresIfNeeded();

            try
            {

                // Initialize logging
                InitializeLogging();
                Logger?.LogInformation("Application starting up...");

                // Managed unhandled exceptions on background threads (e.g. Task.Run without await).
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    var ex = args.ExceptionObject as Exception;
                    Logger?.LogError(ex, "Unhandled background exception");
                    System.Diagnostics.Trace.WriteLine($"UNHANDLED: {ex}");

                    if (args.IsTerminating)
                    {
                        try
                        {
                            Dispatcher?.Invoke(() =>
                                System.Windows.MessageBox.Show(
                                    "An internal error occurred and the emulator had to close.\n\n" +
                                    "Your library and save data are safe. You can re-open the app normally.\n\n" +
                                    $"Detail: {ex?.Message ?? "unknown error"}",
                                    "Emulator Error",
                                    System.Windows.MessageBoxButton.OK,
                                    System.Windows.MessageBoxImage.Warning));
                        }
                        catch { }
                    }
                };

                // Exceptions on the WPF dispatcher thread — mark as handled so the app keeps running.
                DispatcherUnhandledException += (sender, args) =>
                {
                    Logger?.LogError(args.Exception, "Dispatcher unhandled exception");
                    System.Diagnostics.Trace.WriteLine($"DISPATCHER EXCEPTION: {args.Exception}");
                    args.Handled = true;
                };

                base.OnStartup(e);

                // Seed default theme resources before the window loads so DynamicResource
                // bindings (including LibraryCardWidth) are never unset on first render.
                Current.Resources["LibraryCardWidth"] = 148.0;

                // Load config before showing the window so saved bounds are available.
                await InitializeConfigurationAsync();

                Logger?.LogInformation("Creating main window...");
                var mainWindow = new MainWindow();
                mainWindow.Show();
                Logger?.LogInformation("Main window shown");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize application");
                MessageBox.Show($"Failed to start application: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void InitializeLogging()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
            Logger = loggerFactory.CreateLogger<App>();
        }

        /// <summary>
        /// One-time migration: pre-v1.3.3 portable installs kept Cores at [exe]/Cores/.
        /// The new layout puts them under [DataRoot]/Cores/ so PortableData/ holds the
        /// entire portable experience. Move any cores from the legacy location on first
        /// launch with the new code; idempotent — does nothing if already migrated.
        ///
        /// Shows a small "migrating" splash when the total payload is large enough to
        /// take noticeable time on slow USB media (>100MB threshold) so the user knows
        /// the app is working, not hung.
        /// </summary>
        private static void MigratePortableCoresIfNeeded()
        {
            if (!AppPaths.IsPortable) return;
            try
            {
                string? exeFolder = AppPaths.GetExeFolderIfPortable();
                if (string.IsNullOrEmpty(exeFolder)) return;
                string legacyCores = Path.Combine(exeFolder, "Cores");
                string newCores    = AppPaths.GetCoresFolder();

                // Same path → nothing to migrate (sanity check)
                if (string.Equals(Path.GetFullPath(legacyCores).TrimEnd('\\'),
                                  Path.GetFullPath(newCores).TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase))
                    return;

                if (!Directory.Exists(legacyCores)) return;

                var legacyDlls = Directory.EnumerateFiles(legacyCores, "*.dll", SearchOption.TopDirectoryOnly).ToList();
                if (legacyDlls.Count == 0) return;

                long totalBytes = 0;
                foreach (string dll in legacyDlls)
                {
                    try { totalBytes += new FileInfo(dll).Length; } catch { }
                }

                // Threshold: 100MB. Below this, the move is fast enough on typical media that
                // a splash creates more confusion than it resolves.
                const long SPLASH_THRESHOLD = 100L * 1024 * 1024;
                Window? splash = null;
                System.Windows.Controls.TextBlock? splashText = null;
                if (totalBytes >= SPLASH_THRESHOLD)
                {
                    // Splash matches the app's default dark theme: bg #1F1F21, text white,
                    // muted text #CCCCCC, accent red #E03535 border for the brand cue.
                    var bgBrush     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x21));
                    var mutedBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
                    var accentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x35, 0x35));

                    splash = new Window
                    {
                        Title = "Emutastic — Setting up portable mode",
                        Width = 380,
                        Height = 130,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        Background = bgBrush,
                        Topmost = true,
                    };
                    var border = new System.Windows.Controls.Border
                    {
                        BorderBrush = accentBrush,
                        BorderThickness = new Thickness(1),
                        Background = bgBrush,
                    };
                    var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
                    stack.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = "Setting up portable mode…",
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new Thickness(0, 0, 0, 8),
                    });
                    splashText = new System.Windows.Controls.TextBlock
                    {
                        Text = $"Moving cores into PortableData… (0 / {legacyDlls.Count})",
                        FontSize = 12,
                        Foreground = mutedBrush,
                    };
                    stack.Children.Add(splashText);
                    border.Child = stack;
                    splash.Content = border;
                    splash.Show();
                }

                // File moves run on a worker thread so the splash UI can repaint as
                // progress updates. Dispatcher.Invoke from the same thread that called
                // splash.Show() would block until the loop finished — splash would draw
                // once at "0/N" and never update.
                int moved = 0;
                var doneFrame = new System.Windows.Threading.DispatcherFrame();
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        foreach (string dll in legacyDlls)
                        {
                            string dest = Path.Combine(newCores, Path.GetFileName(dll));
                            try
                            {
                                // If a core with the same name already exists in the new folder, keep it
                                // (user may have re-downloaded it after manually moving). Delete the legacy copy.
                                if (File.Exists(dest))
                                    File.Delete(dll);
                                else
                                    File.Move(dll, dest);
                                System.Threading.Interlocked.Increment(ref moved);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.WriteLine($"Cores migration: failed to move {Path.GetFileName(dll)} — {ex.Message}");
                            }

                            if (splashText != null)
                            {
                                int captured = moved;
                                splashText.Dispatcher.BeginInvoke(new Action(() =>
                                    splashText.Text = $"Moving cores into PortableData… ({captured} / {legacyDlls.Count})"));
                            }
                        }
                    }
                    finally
                    {
                        // Stop pumping the dispatcher so OnStartup can continue.
                        if (splashText != null)
                            splashText.Dispatcher.BeginInvoke(new Action(() => doneFrame.Continue = false));
                        else
                            doneFrame.Continue = false;
                    }
                });

                if (splash != null)
                    System.Windows.Threading.Dispatcher.PushFrame(doneFrame);
                else
                {
                    // No splash means no dispatcher pumping; just block on the task.
                    while (doneFrame.Continue)
                        System.Threading.Thread.Sleep(20);
                }

                splash?.Close();
                System.Diagnostics.Trace.WriteLine($"Portable cores migration: moved {moved} core(s) from {legacyCores} → {newCores}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Portable cores migration failed: {ex.Message}");
            }
        }

        private async Task InitializeConfigurationAsync()
        {
            try
            {
                Configuration = new JsonConfigurationService(Logger as ILogger<JsonConfigurationService>);
                await Configuration.LoadAsync();
                var prefs = Configuration.GetUserPreferences();
                AppPaths.SetCustomRoot(prefs.CustomDataDirectory);
                AppPaths.SetScreenshotsFolder(prefs.ScreenshotsFolder);
                AppPaths.SetRecordingsFolder(prefs.RecordingsFolder);

                // First-run: let user pick data directory before anything creates folders.
                // Skipped in portable mode — that mode implies "use the folder beside the .exe".
                if (!AppPaths.IsPortable
                    && string.IsNullOrEmpty(prefs.CustomDataDirectory)
                    && !File.Exists(Path.Combine(AppPaths.DataRoot, "library.db")))
                {
                    var result = System.Windows.MessageBox.Show(
                        "Choose where to store your library (database, saves, artwork, snaps).\n\n" +
                        $"Click Yes to browse, or No to use the default:\n{AppPaths.DefaultRoot}",
                        "Welcome to Emutastic",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question);

                    if (result == System.Windows.MessageBoxResult.Yes)
                    {
                        var folderDlg = new Microsoft.Win32.OpenFolderDialog
                        {
                            Title = "Select data directory"
                        };
                        if (folderDlg.ShowDialog() == true)
                        {
                            string chosen = folderDlg.FolderName;

                            // Detect existing Emutastic data at the chosen location
                            bool hasExistingData = Directory.Exists(Path.Combine(chosen, "Artwork"))
                                || Directory.Exists(Path.Combine(chosen, "BatterySaves"))
                                || Directory.Exists(Path.Combine(chosen, "Save States"))
                                || Directory.Exists(Path.Combine(chosen, "Snaps"));
                            bool hasDb = File.Exists(Path.Combine(chosen, "library.db"));

                            if (hasExistingData && !hasDb)
                            {
                                System.Windows.MessageBox.Show(
                                    "Existing Emutastic data found at this location (artwork, saves, etc.).\n\n" +
                                    "A new library database will be created. Import your games and existing artwork will be discovered automatically.",
                                    "Existing Data Found", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                                FirstRunDiscoveryNeeded = true;
                            }

                            prefs.CustomDataDirectory = chosen;
                            Configuration.SetUserPreferences(prefs);
                            await Configuration.SaveAsync();
                            AppPaths.SetCustomRoot(chosen);
                        }
                    }
                }

                CoreOptions = new CoreOptionsService();
                ApplyThemeResources();

                // Apply saved theme colors via ThemeService
                var themeConfig = Configuration.GetThemeConfiguration();
                var themeSvc = Services.ThemeService.Instance;
                themeSvc.ScanInstalledThemes();
                themeSvc.LoadAndApplyTheme(themeConfig.ActiveThemeId);

                Logger?.LogInformation("Configuration system initialized successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize configuration system");
                System.Diagnostics.Trace.WriteLine($"CONFIG INIT FAILED: {ex.Message}");
                // Don't replace Configuration — if LoadAsync partially succeeded,
                // the existing instance still has the loaded data.
                // Only create a fallback if Configuration is null.
                Configuration ??= new JsonConfigurationService(null);
            }
        }

        /// <summary>
        /// Pushes saved theme layout values into Application.Current.Resources so that all
        /// {DynamicResource} bindings (grid padding, card spacing) update immediately.
        /// Safe to call from any thread before or after the window is shown.
        /// </summary>
        public static void ApplyThemeResources()
        {
            var theme = Configuration?.GetThemeConfiguration() ?? new Emutastic.Configuration.ThemeConfiguration();

            // Clamp to safe limits so malformed config can't break the layout.
            int padding   = Math.Clamp(theme.GridPadding, 8, 64);
            int spacing   = Math.Clamp(theme.CardSpacing, 4, 48);
            int cardWidth = Math.Clamp(theme.CardWidth, 148, 280);

            Current.Resources["LibraryGridPadding"] = new System.Windows.Thickness(padding);
            Current.Resources["LibraryCardMargin"]  = new System.Windows.Thickness(0, 0, spacing, spacing);
            Current.Resources["LibraryCardWidth"]   = (double)cardWidth;
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                if (Configuration != null)
                    await Configuration.SaveAsync();
            }
            catch { }

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);
        }
    }
}
