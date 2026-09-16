using Emutastic.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Emutastic.Services
{
    /// <summary>
    /// Starts Windows apps from the library. An app runs as its own process, exactly as if it
    /// were opened from Explorer — shortcuts keep their arguments and start-in folder. The
    /// library records the launch and, when the shell hands the process back, how long it ran.
    /// </summary>
    public static class WindowsAppLauncher
    {
        /// <summary>A started app. <paramref name="Tracked"/> is false when the shell didn't hand
        /// back a process to follow — a launcher link such as steam://, or an app that passed the
        /// request to a copy of itself that was already running.</summary>
        public sealed record LaunchedApp(bool Tracked);

        /// <param name="onExited">Called on the UI thread with the seconds the app ran, once its
        /// process ends. Never called for an untracked launch.</param>
        /// <returns>Null when the app could not be started (the user has been told why).</returns>
        public static LaunchedApp? Launch(Game game, Window? owner, DatabaseService? db = null, Action<int>? onExited = null)
        {
            string path = game.RomPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Show(owner, $"This app can't be found:\n{path}\n\nIt may have been moved or uninstalled. " +
                            "Remove it from the library and add it again from its new location.",
                     "App Not Found", MessageBoxImage.Warning);
                return null;
            }

            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            // Programs and batch files expect to start in their own folder (games load their data
            // relative to it); shortcuts carry their own start-in folder.
            if (!WindowsApps.IsShortcut(path))
                psi.WorkingDirectory = Path.GetDirectoryName(path) ?? "";

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return null;   // ERROR_CANCELLED: the user declined the administrator prompt
            }
            catch (Exception ex)
            {
                Show(owner, $"Couldn't start {game.Title}:\n\n{ex.Message}", "Launch Error", MessageBoxImage.Error);
                return null;
            }

            DateTime startedUtc = DateTime.UtcNow;
            try
            {
                db ??= new DatabaseService();
                db.UpdatePlayCount(game.Id);
            }
            catch (Exception ex) { Trace.WriteLine($"[WindowsApp] play count: {ex.Message}"); }
            game.PlayCount++;
            game.LastPlayed = DateTime.Now;

            string pid = "none";
            try { if (process != null) pid = process.Id.ToString(); } catch { /* exited already */ }
            Trace.WriteLine($"[WindowsApp] started \"{game.Title}\" ({path}) pid={pid}");

            if (process == null) return new LaunchedApp(Tracked: false);

            var dispatcher = Application.Current?.Dispatcher;
            var statsDb = db;
            _ = Task.Run(async () =>
            {
                int seconds = 0;
                try
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    seconds = (int)Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);
                    statsDb?.UpdatePlayTime(game.Id, seconds);
                    Trace.WriteLine($"[WindowsApp] \"{game.Title}\" exited after {seconds}s");
                }
                catch (Exception ex) { Trace.WriteLine($"[WindowsApp] waiting for \"{game.Title}\": {ex.Message}"); }
                finally { process.Dispose(); }

                void Finish()
                {
                    game.TotalPlayTimeSeconds += seconds;
                    try { onExited?.Invoke(seconds); }
                    catch (Exception ex) { Trace.WriteLine($"[WindowsApp] onExited: {ex.Message}"); }
                }
                try
                {
                    if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.Invoke(Finish);
                    else Finish();
                }
                catch { /* the app is closing */ }
            });
            return new LaunchedApp(Tracked: true);
        }

        private static void Show(Window? owner, string message, string caption, MessageBoxImage icon)
        {
            if (owner != null) MessageBox.Show(owner, message, caption, MessageBoxButton.OK, icon);
            else MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
        }
    }
}
