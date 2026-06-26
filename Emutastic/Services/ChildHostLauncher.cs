using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Launches a game in a separate <c>Emutastic.exe --emuhost</c> child process (the app's own
    /// EmulatorWindow, hosted out-of-process). Running the libretro core in a fresh process is what
    /// makes PS2/LRPS2 boot crash-free (~70% in-process → ~4%, → ~0.16% with the one-shot retry here).
    /// Supervised off the UI thread so the main app stays usable while the game runs; on a clean
    /// session end it reports play-seconds back on the UI thread for stats ingest.
    /// </summary>
    public static class ChildHostLauncher
    {
        // A boot crash dies within a few seconds; a real session that ends this fast was a
        // quick manual close, not a crash worth retrying.
        private const int BootCrashSeconds = 20;
        // Subtracted from wall-clock to approximate actual play time (process + WPF + boot overhead).
        private const int StartupOverheadSeconds = 3;

        /// <param name="onSessionEnded">Invoked on the UI thread with play-seconds (0 if it never
        /// booted) after the child exits — caller ingests play-stats / refreshes UI.</param>
        /// <param name="fullscreen">Start the child window fullscreen (EmuTV / couch mode).</param>
        public static void Launch(Models.Game game, string corePath, string? loadStatePath,
                                  Action<int>? onSessionEnded, bool fullscreen = false)
        {
            string exe;
            try { exe = Process.GetCurrentProcess().MainModule!.FileName; }
            catch (Exception ex) { Trace.WriteLine($"[ChildHost] can't resolve exe: {ex.Message}"); onSessionEnded?.Invoke(0); return; }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            Task.Run(() =>
            {
                int secs = 0;
                try { secs = Supervise(exe, game, corePath, loadStatePath, fullscreen); }
                catch (Exception ex) { Trace.WriteLine($"[ChildHost] supervise failed: {ex}"); }
                if (onSessionEnded != null)
                {
                    try
                    {
                        if (dispatcher != null) dispatcher.Invoke(() => onSessionEnded(secs));
                        else onSessionEnded(secs);
                    }
                    catch (Exception ex) { Trace.WriteLine($"[ChildHost] onSessionEnded: {ex.Message}"); } // main app may have closed
                }
            });
        }

        private static int Supervise(string exe, Models.Game game, string corePath, string? loadStatePath, bool fullscreen)
        {
            string resultPath = Path.Combine(Path.GetTempPath(), $"emuhost_{Guid.NewGuid():N}.json");

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",   // so the child finds Cores/, Native/, config
                };
                psi.ArgumentList.Add("--emuhost");
                // Inherit portable mode (the child resolves the same DataRoot). portable.txt is
                // auto-detected by the exe folder; the --portable arg path needs forwarding.
                if (AppPaths.IsPortable) psi.ArgumentList.Add("--portable");
                psi.ArgumentList.Add("--rom");       psi.ArgumentList.Add(game.RomPath);
                psi.ArgumentList.Add("--console");   psi.ArgumentList.Add(game.Console);
                psi.ArgumentList.Add("--game-id");   psi.ArgumentList.Add(game.Id.ToString());
                psi.ArgumentList.Add("--core");      psi.ArgumentList.Add(corePath);
                psi.ArgumentList.Add("--title");     psi.ArgumentList.Add(game.Title ?? "");
                psi.ArgumentList.Add("--rom-hash");  psi.ArgumentList.Add(game.RomHash ?? "");
                psi.ArgumentList.Add("--result");    psi.ArgumentList.Add(resultPath);
                if (!string.IsNullOrEmpty(loadStatePath))
                {
                    psi.ArgumentList.Add("--load-state");
                    psi.ArgumentList.Add(loadStatePath);
                }
                if (fullscreen) psi.ArgumentList.Add("--fullscreen");

                var sw = Stopwatch.StartNew();
                try
                {
                    using var proc = Process.Start(psi);
                    if (proc == null) return 0;
                    proc.WaitForExit();
                }
                catch (Exception ex) { Trace.WriteLine($"[ChildHost] start failed: {ex.Message}"); return 0; }
                sw.Stop();

                string status = "crash";
                try
                {
                    if (File.Exists(resultPath))
                    {
                        string t = File.ReadAllText(resultPath);
                        status = t.Contains("\"ok\"") ? "ok" : t.Contains("\"error\"") ? "error" : "crash";
                    }
                }
                catch { }
                try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }

                int secs = Math.Max(0, (int)sw.Elapsed.TotalSeconds - StartupOverheadSeconds);
                Trace.WriteLine($"[ChildHost] {game.Title}: status={status} ran={sw.Elapsed.TotalSeconds:F0}s attempt={attempt}");

                if (status == "ok") return secs;          // clean session — count play time
                if (status == "error") return 0;          // setup error (bad rom/core) — don't retry

                // crash: retry ONCE, only if it died early (a boot crash). A late crash = treat as played.
                if (attempt == 0 && sw.Elapsed.TotalSeconds < BootCrashSeconds)
                {
                    Trace.WriteLine($"[ChildHost] boot crash — retrying once");
                    continue;
                }
                return secs;
            }
            return 0;
        }
    }
}
