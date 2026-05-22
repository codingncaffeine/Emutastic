using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Emutastic.Services
{
    /// <summary>
    /// Diagnostic background watchdog that detects UI dispatcher stalls and
    /// logs each freeze (start window + total duration) to ui_freezes.log.
    /// Re-enabled to catch any regressions after the LibVLC warmup / off-UI
    /// bitmap-setup fixes. Delete the file + the one line in App.OnStartup
    /// when no longer needed.
    ///
    /// Mechanism: a background thread posts a <see cref="DispatcherPriority.Input"/>
    /// ping at the top of each iteration and waits for it to complete. If the
    /// ping doesn't complete within <see cref="FreezeThresholdMs"/>, the
    /// watchdog captures the active-window snapshot from the most recent
    /// successful ping (functionally "window at freeze start") and keeps
    /// polling until the ping clears. On unfreeze it writes one line with
    /// the total duration.
    /// </summary>
    internal sealed class UiFreezeWatchdog
    {
        private const int FreezeThresholdMs = 500;
        private const int PollIntervalMs    = 100;

        public static UiFreezeWatchdog Instance { get; } = new();

        private Dispatcher? _dispatcher;
        private Thread?     _thread;
        private string?     _logPath;
        private volatile bool _stopRequested;

        private readonly Stopwatch _sw = Stopwatch.StartNew();

        // Updated on UI thread inside the ping callback. The watchdog reads it
        // when a freeze is detected — at that moment the field holds the title
        // captured by the LAST successful ping, which is functionally "active
        // window at the moment the freeze began."
        private volatile string _currentWindowTitle = "(none)";

        // UI thread writes via Interlocked.Exchange; watchdog reads via
        // Interlocked.Read. Represents Stopwatch elapsed-ms at the moment the
        // most recent ping ran on the dispatcher.
        private long _lastPingCompletedMs;

        private UiFreezeWatchdog() { }

        public void Start(Dispatcher dispatcher)
        {
            if (_thread != null) return;
            _dispatcher = dispatcher;
            try
            {
                _logPath = Path.Combine(AppPaths.GetFolder("Logs"), "ui_freezes.log");
                LogRotation.RotateIfLarge(_logPath);
                // Header line per session so freezes from different runs are obvious.
                File.AppendAllText(_logPath,
                    $"=== Watchdog start {DateTime.Now:yyyy-MM-dd HH:mm:ss} (threshold {FreezeThresholdMs}ms) ==={Environment.NewLine}");
            }
            catch { /* if we can't write the log we still want the thread alive in case dir appears */ }

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "UiFreezeWatchdog",
                Priority = ThreadPriority.AboveNormal, // small bump so OS scheduling doesn't add fake latency
            };
            _thread.Start();
        }

        public void Stop() => _stopRequested = true;

        private void Loop()
        {
            while (!_stopRequested)
            {
                try { RunOneIteration(); }
                catch (TaskCanceledException) { return; }
                catch { /* never let this thread die — diagnostics are best-effort */ }
            }
        }

        private void RunOneIteration()
        {
            var disp = _dispatcher;
            if (disp == null || disp.HasShutdownStarted || disp.HasShutdownFinished)
            {
                Thread.Sleep(500);
                return;
            }

            long pingPostedMs = _sw.ElapsedMilliseconds;

            // Input priority reflects "is the UI responsive to the user"; Background
            // would routinely sit behind legitimate layout/render and produce false
            // positives.
            // BeginInvoke can throw if shutdown began between the HasShutdownStarted
            // check above and this call. Without this guard the inner poll loop
            // would spin forever waiting for a ping that can never run.
            try
            {
                disp.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    Interlocked.Exchange(ref _lastPingCompletedMs, _sw.ElapsedMilliseconds);
                    UpdateActiveWindowSnapshot();
                }));
            }
            catch (InvalidOperationException) { Thread.Sleep(500); return; }
            catch (TaskCanceledException)     { return; }

            // Poll until the ping completes or the threshold expires.
            while (!_stopRequested)
            {
                Thread.Sleep(PollIntervalMs);

                long completedMs = Interlocked.Read(ref _lastPingCompletedMs);
                if (completedMs >= pingPostedMs) return;        // healthy round-trip

                long ageMs = _sw.ElapsedMilliseconds - pingPostedMs;
                if (ageMs < FreezeThresholdMs) continue;

                // ── Freeze detected ──
                string atWindow = _currentWindowTitle;
                long  freezeStartMs = pingPostedMs;

                // Wait for the stuck ping to drain. Keep checking; don't spam
                // additional pings into the queue.
                while (!_stopRequested)
                {
                    Thread.Sleep(PollIntervalMs);
                    completedMs = Interlocked.Read(ref _lastPingCompletedMs);
                    if (completedMs >= pingPostedMs) break;
                }

                long durMs = completedMs - freezeStartMs;
                AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FREEZE {durMs}ms  window={atWindow}");
                return;
            }
        }

        private void UpdateActiveWindowSnapshot()
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;
                Window? active = null;
                foreach (Window w in app.Windows)
                {
                    if (w.IsActive) { active = w; break; }
                }
                _currentWindowTitle = active != null
                    ? $"{active.GetType().Name}(\"{active.Title}\")"
                    : "(none)";
            }
            catch { }
        }

        private void AppendLog(string line)
        {
            try
            {
                if (_logPath == null)
                {
                    _logPath = Path.Combine(AppPaths.GetFolder("Logs"), "ui_freezes.log");
                }
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
