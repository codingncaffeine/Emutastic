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

        /// <summary>
        /// Mirror of <c>MainWindow._currentNavTag</c> when the user is on a console
        /// view (null otherwise). Read by <see cref="ApplyLayoutResources"/> so it can
        /// honor the active console's <c>PerConsoleSpacing</c> override even when the
        /// trigger is a global event like prefs save or theme change.
        ///
        /// Without this, two writers (PreferencesWindow.ThemeSaveBtn_Click → ApplyThemeResources
        /// and MainWindow.SpacingSliderToolbar_ValueChanged) race on LibraryCardMargin and
        /// the global-default writer wins, silently stomping the toolbar's per-console value
        /// until the user navigates away and back.
        /// </summary>
        public static string? ActiveConsoleTag { get; set; }

        private static Mutex? _singleInstanceMutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Separate-process game host: boot ONE game in EmulatorWindow with no MainWindow/
            // library. Lets the libretro core run in a clean child process (the LRPS2 boot-crash
            // fix). No single-instance guard so it can run alongside the main app / in parallel.
            if (e.Args.Contains("--emuhost"))
            {
                await RunEmuHostAsync(e);
                return;
            }

            // Headless controller → player routing self-test over SDL3 virtual joysticks:
            //   Emutastic.exe --selftest-input [report.log] [--portable]
            // No window, never touches the user's configuration, exit code 0 = every check
            // passed. Handled before the single-instance guard so a copy of the app that is
            // already open cannot swallow it. Portable detection and the SDL3.dll resolver are
            // the only startup steps it needs.
            int selfTestIdx = Array.FindIndex(e.Args,
                a => string.Equals(a, "--selftest-input", StringComparison.OrdinalIgnoreCase));
            if (selfTestIdx >= 0)
            {
                AppPaths.DetectPortableMode(e.Args);
                InstallSdl3Resolver();
                string? report = selfTestIdx + 1 < e.Args.Length
                                 && !e.Args[selfTestIdx + 1].StartsWith("--", StringComparison.Ordinal)
                    ? e.Args[selfTestIdx + 1]
                    : null;
                Environment.Exit(InputSelfTest.Run(report));
                return;
            }

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
            // routes to PortableData instead of %AppData%. Two triggers, either
            // one activates: drop a portable.txt next to the .exe, OR pass
            // --portable on the command line.
            AppPaths.DetectPortableMode(e.Args);

            // Capture Trace diagnostics (core load, game launch, decode errors) to a file. In a
            // Release build these otherwise go nowhere, so failures can't be diagnosed after the
            // fact. Truncated each launch; flushed immediately so the latest session is readable
            // even while the app is still running.
            try
            {
                string logDir = System.IO.Path.Combine(AppPaths.DataRoot, "Logs");
                System.IO.Directory.CreateDirectory(logDir);
                var writer = new System.IO.StreamWriter(System.IO.Path.Combine(logDir, "emutastic.log"), append: false) { AutoFlush = true };
                System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(writer));
                System.Diagnostics.Trace.WriteLine($"=== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} | v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} ===");
            }
            catch { /* logging is best-effort */ }

            // WPF's BitmapImage UriSource downloads go through .NET's classic
            // WebRequest pool, which defaults to 2 connections per host. The
            // Achievements tab's trophy case can request 100+ tiles from
            // media.retroachievements.org at once — without this bump the
            // download trickles in two-at-a-time and the wall takes minutes
            // to fully paint. 12 is comfortably above the host's per-IP cap
            // and matches what modern browsers use.
            System.Net.ServicePointManager.DefaultConnectionLimit = 12;

            // Clean up leftover update artifacts from a previous auto-update
            Services.UpdateService.CleanupOldFiles();

            // Portable mode v2 (v1.3.3): cores moved from [exe]/Cores/ → [DataRoot]/Cores/
            // so the entire portable experience sits inside PortableData/. Migrate any
            // pre-existing cores from the old location on first launch with the new code.
            MigratePortableCoresIfNeeded();

            // v1.4.6: SDL3.dll, ffmpeg.exe, and DATs/ moved out of the .exe folder and
            // under [DataRoot] so they survive UAC-restricted installs (Program Files)
            // and version upgrades where the user extracts the new release into a fresh
            // folder. Install the resolver here (early) so SDL3 P/Invokes work regardless
            // of where the .dll ends up.
            //
            // Migration itself runs AFTER config load — see below — so it sees the user's
            // final DataRoot (custom data directory applied) instead of relocating things
            // to the default %AppData% path that would then be stranded when the custom
            // root is applied a moment later.
            InstallSdl3Resolver();

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

                // Pre-Main cold-load: the gap between OS process creation
                // (Process.StartTime) and the first line of our code. Captures
                // everything the in-app trace can't see — runtime load,
                // single-file self-extract, startup-path JIT. On a warm launch
                // this is a few hundred ms; a big value here is the fingerprint
                // of an intermittent slow cold start (large self-contained bundle
                // paged off a cold disk). Logged first so it heads the session.
                try
                {
                    var preMainMs = (DateTime.Now - System.Diagnostics.Process
                        .GetCurrentProcess().StartTime).TotalMilliseconds;
                    Services.StartupTrace.Mark($"preMain_cold_load_ms={preMainMs:F0}");
                }
                catch { /* diagnostic only */ }

                // Seed default theme resources before the window loads so DynamicResource
                // bindings (including LibraryCardWidth) are never unset on first render.
                Current.Resources["LibraryCardWidth"] = 148.0;

                // Load config before showing the window so saved bounds are available.
                var swCfg = Services.StartupTrace.Start();
                await InitializeConfigurationAsync();
                Services.StartupTrace.Stop("InitializeConfigurationAsync", swCfg);

                // Native-assets migration runs HERE (after config load) so it sees the
                // user's final DataRoot — including any custom data directory applied by
                // InitializeConfigurationAsync via AppPaths.SetCustomRoot. Earlier we ran
                // this before config and it stranded assets at the default %AppData% path
                // whenever a user had a custom data directory configured.
                var swMig = Services.StartupTrace.Start();
                MigrateNativeAssetsIfNeeded();
                Services.StartupTrace.Stop("MigrateNativeAssetsIfNeeded", swMig);

                // Start the UI freeze watchdog BEFORE the main window so any
                // freeze during the first render is logged. Diagnostic-only —
                // delete `Services/UiFreezeWatchdog.cs` and this line when the
                // freeze hunt is over.
                Services.UiFreezeWatchdog.Instance.Start(Dispatcher);
                Services.StartupTrace.Mark("watchdog_started");

                Logger?.LogInformation("Creating main window...");
                var swMainWindowCtor = Services.StartupTrace.Start();
                var mainWindow = new MainWindow();
                Services.StartupTrace.Stop("MainWindow.ctor", swMainWindowCtor);

                var swShow = Services.StartupTrace.Start();
                mainWindow.Show();
                Services.StartupTrace.Stop("MainWindow.Show", swShow);
                Logger?.LogInformation("Main window shown");

                // Warm LibVLC off the UI thread so the first game-detail open
                // doesn't pay its multi-second native init cost on the dispatcher.
                Services.VideoPlaybackService.Instance.StartWarmup();
                Services.StartupTrace.Mark("libvlc_warmup_kicked");

                Services.GitHubSyncService.Instance.LoadFromConfig();
                if (Services.GitHubSyncService.Instance.IsAuthenticated)
                    _ = Task.Run(async () =>
                    {
                        // Delay cloud sync init so library rendering and artwork
                        // loading aren't competing with network calls on first launch.
                        await Task.Delay(TimeSpan.FromSeconds(10));
                        await Services.GitHubSyncService.Instance.ValidateTokenAsync();
                        if (Services.GitHubSyncService.Instance.IsAuthenticated)
                        {
                            await Services.GitHubSyncService.Instance.LoadManifestAsync();
                            // Auth + manifest are ready now — THIS is where the
                            // background full-sync belongs (MainWindow.OnLoaded was
                            // too early: no token yet, so it bailed and never ran).
                            Services.GitHubSyncService.Instance.StartBackgroundSync(new Services.DatabaseService());
                        }
                    });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize application");
                MessageBox.Show($"Failed to start application: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        // ── Separate-process game host (--emuhost) ─────────────────────────────
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);

        private static bool _emuHostResultWritten;
        private static string? _emuHostResultPath;

        private static string? EmuArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void EmuHostResult(string status, string? detail)
        {
            if (_emuHostResultWritten || string.IsNullOrEmpty(_emuHostResultPath)) return;
            _emuHostResultWritten = true;
            try
            {
                string d = detail == null ? "null" : "\"" + detail.Replace("\\", "/").Replace("\"", "'") + "\"";
                File.WriteAllText(_emuHostResultPath + ".tmp", "{ \"status\": \"" + status + "\", \"detail\": " + d + " }");
                File.Move(_emuHostResultPath + ".tmp", _emuHostResultPath, true);
            }
            catch { }
        }

        private static void EmuHardExit(uint code) { try { TerminateProcess(GetCurrentProcess(), code); } catch { } }

        /// <summary>
        /// Boots one game in EmulatorWindow with no MainWindow/library — the clean child
        /// process. Survives quit-after seconds => writes status "ok"; a boot crash kills the
        /// process (no result) or the managed handler writes "crash". WER dialogs suppressed so
        /// a crash never hangs a benchmark.
        /// </summary>
        private async System.Threading.Tasks.Task RunEmuHostAsync(StartupEventArgs e)
        {
            SetErrorMode(0x0001 | 0x0002); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX

            string? rom = EmuArg(e.Args, "--rom");
            string console = EmuArg(e.Args, "--console") ?? "PS2";
            string? core = EmuArg(e.Args, "--core");
            _emuHostResultPath = EmuArg(e.Args, "--result");
            int quitAfter = int.TryParse(EmuArg(e.Args, "--quit-after"), out var q) ? q : 0;

            if (!System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Trace.Listeners.Clear();
                System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener(useErrorStream: true));
            }

            AppPaths.DetectPortableMode(e.Args);
            InitializeLogging();

            // Boot crash on any managed thread: record + hard-exit (NO MessageBox, unlike normal mode).
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            {
                EmuHostResult("crash", (a.ExceptionObject as Exception)?.Message);
                EmuHardExit(0xC0000005);
            };
            DispatcherUnhandledException += (_, a) => { a.Handled = true; };

            try
            {
                base.OnStartup(e);
                InstallSdl3Resolver();
                try { await InitializeConfigurationAsync(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[emuhost] config: {ex.Message}"); }
                MigrateNativeAssetsIfNeeded();

                if (string.IsNullOrEmpty(rom) || !File.Exists(rom)) { EmuHostResult("error", "rom missing"); EmuHardExit(2); return; }
                if (string.IsNullOrEmpty(core) || !File.Exists(core)) { EmuHostResult("error", "core missing"); EmuHardExit(2); return; }

                string? title = EmuArg(e.Args, "--title");
                string romHash = EmuArg(e.Args, "--rom-hash") ?? "";
                string? loadState = EmuArg(e.Args, "--load-state");

                // Preserve cloud save-sync on session end (EmulatorWindow's close path uses it).
                try { Services.GitHubSyncService.Instance.LoadFromConfig(); } catch { }

                // Real library id so the child's DB writes (save-state rows have a FK to the
                // game, per-game window size, play-stats) resolve. Cross-process writes are safe
                // — the DB is WAL with a 5s busy_timeout. The child owns these writes; the parent
                // no longer writes play-stats (would double-count).
                int gameId = int.TryParse(EmuArg(e.Args, "--game-id"), out var gid) ? gid : 0;
                var game = new Models.Game
                {
                    Id = gameId,
                    Title = string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(rom) : title,
                    Console = console, RomPath = rom, RomHash = romHash,
                };
                Views.EmulatorWindow.FreeStaleDll();
                var libCore = new Services.LibretroCore(core);
                var win = new Views.EmulatorWindow(game, libCore,
                    string.IsNullOrEmpty(loadState) ? null : loadState);
                if (e.Args.Contains("--fullscreen")) win.StartInFullscreen = true;   // EmuTV / couch mode
                // Clean session end (user closed the window): signal the parent so it ingests
                // play-time and does NOT auto-retry. WPF then shuts the process down normally.
                win.Closed += (_, _) => EmuHostResult("ok", null);
                Current.MainWindow = win;
                win.Show();

                if (quitAfter > 0)
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(quitAfter) };
                    t.Tick += (_, _) => { t.Stop(); EmuHostResult("ok", null); EmuHardExit(0); };
                    t.Start();
                }
            }
            catch (Exception ex)
            {
                EmuHostResult("error", ex.GetType().Name + ": " + ex.Message);
                EmuHardExit(3);
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

                // Defensive: if the splash transiently became Application.MainWindow
                // (no other window exists yet), null it out before closing so we can't
                // accidentally trigger OnMainWindowClose shutdown before the real
                // MainWindow opens.
                if (splash != null && Application.Current != null
                    && ReferenceEquals(Application.Current.MainWindow, splash))
                {
                    Application.Current.MainWindow = null;
                }
                splash?.Close();
                System.Diagnostics.Trace.WriteLine($"Portable cores migration: moved {moved} core(s) from {legacyCores} → {newCores}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Portable cores migration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Routes [DllImport("SDL3.dll")] calls in ControllerManager to the persistent
        /// [DataRoot]/Native/ location. Returns IntPtr.Zero (i.e. defers to the default
        /// Windows loader) when the file isn't there yet — so legacy installs with
        /// SDL3.dll still sitting next to the .exe keep working until migration moves it.
        /// </summary>
        private static void InstallSdl3Resolver()
        {
            try
            {
                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                    typeof(App).Assembly,
                    (name, _, _) =>
                    {
                        if (!name.Equals("SDL3.dll", StringComparison.OrdinalIgnoreCase)
                         && !name.Equals("SDL3",     StringComparison.OrdinalIgnoreCase))
                            return IntPtr.Zero;

                        string path = Path.Combine(AppPaths.GetNativeFolder(), "SDL3.dll");
                        if (File.Exists(path)
                            && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out var h))
                            return h;
                        return IntPtr.Zero;
                    });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"SDL3 resolver install failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Moves SDL3.dll, ffmpeg.exe, and the DATs/ folder into [DataRoot]/Native/
        /// and [DataRoot]/DATs/ from any of several plausible historical locations:
        ///
        ///   1. The .exe folder itself (legacy pre-v1.4.6 installs that kept
        ///      SDL3.dll, ffmpeg.exe, and DATs/ next to the .exe).
        ///   2. The UAC VirtualStore mirror — Windows silently redirects writes
        ///      to %LOCALAPPDATA%\VirtualStore\&lt;exepath&gt; when the user lacks
        ///      write access to the install dir (Program Files, etc.).
        ///   3. The default %AppData%\Emutastic\ DataRoot — covers the user who
        ///      downloaded DATs/SDL3/ffmpeg while DataRoot was the default and then
        ///      later set CustomDataDirectory to a different folder.
        ///   4. The [exe]\PortableData\ folder — covers the user who used portable
        ///      mode at some point and has since dropped portable.txt.
        ///
        /// Runs AFTER InitializeConfigurationAsync so AppPaths.GetNativeFolder()
        /// and GetDatsFolder() reflect the user's final DataRoot (custom dir
        /// applied). Idempotent — does nothing once the destination is populated
        /// and skips self-copies when a source path equals the destination.
        /// </summary>
        private static void MigrateNativeAssetsIfNeeded()
        {
            try
            {
                string nativeDir = AppPaths.GetNativeFolder();
                string datsDir   = AppPaths.GetDatsFolder();
                string exeDir    = AppPaths.GetExeFolder();

                // UAC VirtualStore mirror path for [exe] writes.
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string virtualStore = string.Empty;
                try
                {
                    string root = Path.GetPathRoot(exeDir) ?? "";
                    if (!string.IsNullOrEmpty(root))
                    {
                        string relative = exeDir.Substring(root.Length);
                        virtualStore = Path.Combine(localAppData, "VirtualStore", relative);
                    }
                }
                catch { }

                // Where SDL3.dll / ffmpeg.exe could live in each candidate source.
                // Legacy sources keep them at the root; DataRoot-style sources keep
                // them under a Native/ subfolder.
                var nativeSourceDirs = new List<string>
                {
                    exeDir,                                                       // legacy [exe]/SDL3.dll
                    virtualStore,                                                 // UAC mirror of above
                    Path.Combine(AppPaths.DefaultRoot, "Native"),                 // [%AppData%/Emutastic]/Native/
                    Path.Combine(exeDir, "PortableData", "Native"),               // [exe]/PortableData/Native/
                };

                // Parent dirs whose DATs/ subfolder we'll scan for *.dat files.
                var datSourceParents = new List<string>
                {
                    exeDir,                                                       // [exe]/DATs/
                    virtualStore,                                                 // [virtualStore]/DATs/
                    AppPaths.DefaultRoot,                                         // [%AppData%/Emutastic]/DATs/
                    Path.Combine(exeDir, "PortableData"),                         // [exe]/PortableData/DATs/
                };

                MigrateSingleFile("SDL3.dll",  nativeSourceDirs.ToArray(), nativeDir);
                MigrateSingleFile("ffmpeg.exe", nativeSourceDirs.ToArray(), nativeDir);
                MigrateDatFolder(datSourceParents.ToArray(), datsDir);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Native assets migration failed: {ex.Message}");
            }
        }

        private static void MigrateSingleFile(string fileName, string[] sourceDirs, string destDir)
        {
            string destPath = Path.Combine(destDir, fileName);
            if (File.Exists(destPath)) return;

            foreach (string src in sourceDirs)
            {
                if (string.IsNullOrEmpty(src)) continue;
                string srcPath = Path.Combine(src, fileName);
                if (!File.Exists(srcPath)) continue;
                // Same path on both sides — nothing to do (covers the case where the
                // user is non-portable but DataRoot resolved to the .exe folder).
                if (string.Equals(Path.GetFullPath(srcPath),
                                  Path.GetFullPath(destPath),
                                  StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    File.Move(srcPath, destPath);
                    System.Diagnostics.Trace.WriteLine($"Migrated {fileName}: {srcPath} → {destPath}");
                    return;
                }
                catch
                {
                    try
                    {
                        File.Copy(srcPath, destPath, overwrite: false);
                        System.Diagnostics.Trace.WriteLine($"Copied {fileName} (source read-only): {srcPath} → {destPath}");
                        return;
                    }
                    catch (Exception ex2)
                    {
                        System.Diagnostics.Trace.WriteLine($"Migrate {fileName} from {src} failed: {ex2.Message}");
                    }
                }
            }
        }

        private static void MigrateDatFolder(string[] sourceDirs, string destDir)
        {
            foreach (string src in sourceDirs)
            {
                if (string.IsNullOrEmpty(src)) continue;
                string srcDats = Path.Combine(src, "DATs");
                if (!Directory.Exists(srcDats)) continue;
                if (string.Equals(Path.GetFullPath(srcDats),
                                  Path.GetFullPath(destDir),
                                  StringComparison.OrdinalIgnoreCase)) return;

                int moved = 0;
                foreach (string dat in Directory.EnumerateFiles(srcDats, "*.dat", SearchOption.TopDirectoryOnly))
                {
                    string destPath = Path.Combine(destDir, Path.GetFileName(dat));
                    if (File.Exists(destPath)) continue;
                    try { File.Move(dat, destPath); moved++; }
                    catch
                    {
                        try { File.Copy(dat, destPath, overwrite: false); moved++; }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Migrate DAT {Path.GetFileName(dat)} failed: {ex.Message}"); }
                    }
                }
                if (moved > 0)
                    System.Diagnostics.Trace.WriteLine($"Migrated {moved} DAT file(s) from {srcDats} → {destDir}");
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
                // Guard a null/empty id (hand-edited config.json or a missing
                // property after a schema bump) — LoadAndApplyTheme can't take
                // null and would throw, skipping theme application entirely.
                if (string.IsNullOrEmpty(themeConfig.ActiveThemeId))
                    themeConfig.ActiveThemeId = "builtin.dark";
                themeSvc.LoadAndApplyTheme(themeConfig.ActiveThemeId);

                // Heal a stale persisted id (e.g. a legacy "custom" sentinel
                // or a deleted custom theme) by writing back whatever
                // LoadAndApplyTheme actually resolved to. Without this, the
                // config keeps a dead id forever and never matches the live state.
                if (themeConfig.ActiveThemeId != themeSvc.ActiveThemeId)
                {
                    themeConfig.ActiveThemeId = themeSvc.ActiveThemeId;
                    Configuration.SetThemeConfiguration(themeConfig);
                    _ = Configuration.SaveAsync();
                }

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
        ///
        /// Backwards-compatible shim — all real work happens in
        /// <see cref="ApplyLayoutResources"/>.
        /// </summary>
        public static void ApplyThemeResources() => ApplyLayoutResources();

        /// <summary>
        /// SOLE writer of <c>LibraryGridPadding</c>, <c>LibraryCardMargin</c>, and
        /// <c>LibraryCardWidth</c>. Reads theme config, applies safety clamps, and
        /// when <see cref="ActiveConsoleTag"/> is set, honors that console's
        /// <c>PerConsoleSpacing</c> override before falling back to the global
        /// <c>CardSpacing</c>.
        ///
        /// Both global writers (theme apply, prefs save) and per-console writers
        /// (toolbar H/V slider) must route through this method. Direct writes to
        /// the three resources from anywhere else will create the multi-writer
        /// bug class that previously stomped per-console margins on prefs save.
        /// </summary>
        public static void ApplyLayoutResources()
        {
            var theme = Configuration?.GetThemeConfiguration() ?? new Emutastic.Configuration.ThemeConfiguration();

            // Clamp to safe limits so malformed config can't break the layout.
            int padding   = Math.Clamp(theme.GridPadding, 8, 64);
            int cardWidth = Math.Clamp(theme.CardWidth, 148, 280);
            var (h, v)    = ResolvePerConsoleSpacing(ActiveConsoleTag);

            Current.Resources["LibraryGridPadding"] = new System.Windows.Thickness(padding);
            Current.Resources["LibraryCardMargin"]  = new System.Windows.Thickness(0, 0, h, v);
            Current.Resources["LibraryCardWidth"]   = (double)cardWidth;
        }

        /// <summary>
        /// Resolve the (H, V) spacing pair for the given console — per-console
        /// override if one exists and parses cleanly, otherwise the global
        /// <c>CardSpacing</c>. Single source of truth for spacing resolution
        /// shared between <see cref="ApplyLayoutResources"/> and MainWindow's
        /// toolbar slider handlers.
        /// </summary>
        public static (int H, int V) ResolvePerConsoleSpacing(string? console)
        {
            var theme = Configuration?.GetThemeConfiguration();
            int globalFallback = Math.Clamp(theme?.CardSpacing ?? 20, 4, 96);
            if (theme == null || string.IsNullOrEmpty(console))
                return (globalFallback, globalFallback);

            if (theme.PerConsoleSpacing != null
                && theme.PerConsoleSpacing.TryGetValue(console, out var raw)
                && raw.Split(',') is var parts && parts.Length == 2
                && int.TryParse(parts[0], out int h)
                && int.TryParse(parts[1], out int v))
            {
                return (Math.Clamp(h, 4, 96), Math.Clamp(v, 4, 96));
            }
            return (globalFallback, globalFallback);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                if (Configuration != null)
                    await Configuration.SaveAsync();
            }
            catch { }

            // Tear down the SDL3 dedicated dispatcher thread cleanly so its
            // hidden HID message-pump window doesn't get terminated mid-frame.
            Emutastic.Services.ControllerManager.ShutdownSdl3Thread();

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);

            // Force-terminate to kill any lingering native worker threads spawned by
            // libretro cores (Dolphin background threads, etc). These are native
            // C++ threads — .NET's IsBackground flag
            // doesn't apply to them — and several heavy cores leave them running
            // after retro_unload_game because we skip context_destroy / FreeLibrary
            // to avoid the on-close NVIDIA driver-callback AV. Without this, the
            // app process can sit at 1+ GB RSS after the WPF UI is gone, blocking
            // rebuilds and confusing the user. Anything we still cared about (config
            // save, mutex release) has already run via base.OnExit by this point.
            Environment.Exit(0);
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
