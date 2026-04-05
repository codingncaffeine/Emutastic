using System.Configuration;
using System.Data;
using System.Windows;
using Emutastic.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using Microsoft.Extensions.Logging.Debug;

namespace Emutastic
{
    public partial class App : Application
    {
        public static IConfigurationService? Configuration { get; private set; }
        public static ILogger? Logger { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
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
                
                InitializeConfigurationAsync().GetAwaiter().GetResult();
                
                // Managed unhandled exceptions on background threads (e.g. Task.Run without await).
                // IsTerminating=true means the CLR has already decided to exit — we can't stop it,
                // but we log it and show a message so the user knows what happened.
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    var ex = args.ExceptionObject as Exception;
                    Logger?.LogError(ex, "Unhandled background exception");
                    System.Diagnostics.Trace.WriteLine($"UNHANDLED: {ex}");

                    if (args.IsTerminating)
                    {
                        // Last chance before the CLR shuts down — show a non-blocking message
                        // on the UI thread so the user isn't left staring at a vanished window.
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
                
                // Initialize and show main window
                Logger?.LogInformation("Creating main window...");
                var mainWindow = new MainWindow();
                Logger?.LogInformation("Showing main window...");
                mainWindow.Show();
                Logger?.LogInformation("Main window shown successfully");
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
            int padding = Math.Clamp(theme.GridPadding, 8, 64);
            int spacing = Math.Clamp(theme.CardSpacing, 4, 48);

            Current.Resources["LibraryGridPadding"] = new System.Windows.Thickness(padding);
            Current.Resources["LibraryCardMargin"]  = new System.Windows.Thickness(0, 0, spacing, spacing);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                if (Configuration != null)
                {
                    await Configuration.SaveAsync();
                    Logger?.LogInformation("Configuration saved on application exit");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to save configuration on exit");
            }

            base.OnExit(e);
        }
    }
}
