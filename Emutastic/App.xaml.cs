using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;
using Emutastic.Configuration;
using Emutastic.Services;
using Microsoft.Extensions.Logging;
using System.IO;
using Microsoft.Extensions.Logging.Debug;

namespace Emutastic
{
    public partial class App : Application
    {
        public static IConfigurationService? Configuration { get; private set; }
        public static ILogger? Logger { get; private set; }
        public static CoreOptionsService CoreOptions { get; private set; } = null!;

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

            try
            {
                // Trace.WriteLine (used throughout libretro callbacks) internally calls
                // OutputDebugStringW, which raises SEH exception 0x4001000a to signal a
                // debugger.  When a debugger IS attached, the debugger catches it silently.
                // When no debugger is attached (running outside VS), the exception propagates
                // through reverse P/Invoke boundaries on native threads (e.g. mupen64plus
                // EmuThread calling our env/log callbacks) and kills the process.
                //
                // Fix: when no debugger is attached, replace DefaultTraceListener
                // (OutputDebugString) with ConsoleTraceListener (writes to stderr, no SEH).
                // This one change makes every Trace.WriteLine in the codebase safe.
                if (!System.Diagnostics.Debugger.IsAttached)
                {
                    System.Diagnostics.Trace.Listeners.Clear();
                    System.Diagnostics.Trace.Listeners.Add(
                        new System.Diagnostics.ConsoleTraceListener(useErrorStream: true));
                }

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

        private async Task InitializeConfigurationAsync()
        {
            try
            {
                Configuration = new JsonConfigurationService(Logger as ILogger<JsonConfigurationService>);
                await Configuration.LoadAsync();
                AppPaths.SetCustomRoot(Configuration.GetUserPreferences().CustomDataDirectory);
                CoreOptions = new CoreOptionsService();
                ApplyThemeResources();
                Logger?.LogInformation("Configuration system initialized successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize configuration system");
                Configuration = new JsonConfigurationService(null);
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
