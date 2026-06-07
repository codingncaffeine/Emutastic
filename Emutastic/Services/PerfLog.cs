using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Per-second framerate log for benchmarking, written to Logs/perf.log.
    /// One line per timer tick: timestamp, console, fps, target, core.Run avg
    /// ms. A session header records the game and the performance-relevant core
    /// options so a log can be read back without guessing what was configured.
    ///
    /// Purpose: close the tweak→measure loop without needing the screen — set
    /// a core-option default, run, then read the average fps out of this file.
    /// Append-only, rotated, never throws. Mirrors the RaLog/CloudSyncLog shape.
    /// </summary>
    public static class PerfLog
    {
        private static readonly object _gate = new();
        private static string? _path;

        private static string Path
        {
            get
            {
                if (_path != null) return _path;
                try { _path = System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "perf.log"); }
                catch { _path = ""; }
                return _path!;
            }
        }

        // Rolling stats for the session summary written at teardown.
        private static int _samples;
        private static double _fpsSum, _fpsMin = double.MaxValue, _fpsMax;

        public static void SessionStart(string header)
        {
            lock (_gate)
            {
                _samples = 0; _fpsSum = 0; _fpsMin = double.MaxValue; _fpsMax = 0;
                Append($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {header} ===");
            }
        }

        public static void Tick(string console, int fps, double targetFps, double coreRunAvgMs)
        {
            lock (_gate)
            {
                // Ignore the 0-fps warmup/stall samples in the average so the
                // summary reflects steady-state, not load hitches.
                if (fps > 0)
                {
                    _samples++;
                    _fpsSum += fps;
                    if (fps < _fpsMin) _fpsMin = fps;
                    if (fps > _fpsMax) _fpsMax = fps;
                }
                Append($"{DateTime.Now:HH:mm:ss} {console} fps={fps} target={targetFps:F0} core.Run={coreRunAvgMs:F1}ms");
            }
        }

        public static void SessionEnd()
        {
            lock (_gate)
            {
                if (_samples > 0)
                    Append($"--- summary: avg={_fpsSum / _samples:F1} min={_fpsMin:F0} max={_fpsMax:F0} over {_samples}s ---");
            }
        }

        private static void Append(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(Path)) return;
                LogRotation.RotateIfLarge(Path);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch { /* never throw from logging */ }
        }
    }
}
