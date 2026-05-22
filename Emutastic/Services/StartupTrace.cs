using System;
using System.Diagnostics;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Diagnostic timing log for app startup phases. Each call writes one line
    /// to <c>startup_timings.log</c> with timestamp, phase name, and elapsed
    /// milliseconds. Pair with <see cref="UiFreezeWatchdog"/>: when the watchdog
    /// reports a multi-second freeze on MainWindow, this log narrows it to which
    /// phase took most of the time.
    ///
    /// Delete the file + every <c>StartupTrace.</c> call when the cause is
    /// identified and fixed.
    /// </summary>
    internal static class StartupTrace
    {
        private static string? _logPath;
        private static bool _sessionHeaderWritten;
        private static readonly object _gate = new();

        /// <summary>
        /// Start a phase timer. Use the returned <see cref="Stopwatch"/> with
        /// <see cref="Stop(string, Stopwatch)"/> to log the elapsed time when
        /// the phase completes.
        /// </summary>
        public static Stopwatch Start() => Stopwatch.StartNew();

        /// <summary>
        /// Log a completed phase. Stops the stopwatch and writes one line:
        /// <c>[2026-05-22 09:02:12.150] phase=LibraryLoad elapsed=234ms</c>.
        /// </summary>
        public static void Stop(string phase, Stopwatch sw)
        {
            sw.Stop();
            Write($"phase={phase} elapsed={sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// Log a one-shot marker without timing (e.g. "main window shown",
        /// "configuration loaded").
        /// </summary>
        public static void Mark(string label) => Write($"mark={label}");

        private static void Write(string body)
        {
            lock (_gate)
            {
                try
                {
                    if (_logPath == null)
                        _logPath = Path.Combine(AppPaths.GetFolder("Logs"), "startup_timings.log");

                    if (!_sessionHeaderWritten)
                    {
                        LogRotation.RotateIfLarge(_logPath);
                        File.AppendAllText(_logPath,
                            $"=== Startup trace {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                        _sessionHeaderWritten = true;
                    }

                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {body}{Environment.NewLine}");
                }
                catch { /* diagnostic — never throw */ }
            }
        }
    }
}
