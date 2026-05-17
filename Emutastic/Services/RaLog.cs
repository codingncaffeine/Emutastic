using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Append-only diagnostic log for the RetroAchievements tab data path.
    /// Routes to [DataRoot]/Logs/ra.log so users running from the release
    /// .exe (not the VS debugger) can still see what the tab is doing when
    /// something looks wrong — Trace.WriteLine in a DefaultTraceListener
    /// goes nowhere visible from a release build.
    ///
    /// Lock guards concurrent writes from the UI thread + background fetch
    /// tasks. Never throws.
    /// </summary>
    public static class RaLog
    {
        private static readonly object _gate = new();
        private static string? _path;

        private static string Path
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    string dir = AppPaths.GetFolder("Logs");
                    _path = System.IO.Path.Combine(dir, "ra.log");
                }
                catch { _path = ""; }
                return _path!;
            }
        }

        public static void Write(string message)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
                lock (_gate)
                {
                    if (string.IsNullOrEmpty(Path)) return;
                    File.AppendAllText(Path, line);
                }
            }
            catch { /* never throw from logging */ }
        }
    }
}
