using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Append-only diagnostic log for cloud save sync, mirroring the RaLog
    /// pattern. Routes to [DataRoot]/Logs/cloudsync.log so sync activity is
    /// visible even when no game session is running (Preferences-triggered
    /// full syncs previously traced into emulator.log only, and only while
    /// a session's Trace listener was attached).
    ///
    /// Each write also echoes to Trace with a [CloudSync] prefix so in-game
    /// queued uploads still interleave with core events in emulator.log,
    /// which is useful for correlating a sync with what the game was doing.
    ///
    /// Lock guards concurrent writes from the UI thread + background sync
    /// tasks. Never throws.
    /// </summary>
    public static class CloudSyncLog
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
                    _path = System.IO.Path.Combine(dir, "cloudsync.log");
                }
                catch { _path = ""; }
                return _path!;
            }
        }

        public static void Write(string message)
        {
            try
            {
                System.Diagnostics.Trace.WriteLine($"[CloudSync] {message}");
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
                lock (_gate)
                {
                    if (string.IsNullOrEmpty(Path)) return;
                    LogRotation.RotateIfLarge(Path);
                    File.AppendAllText(Path, line);
                }
            }
            catch { /* never throw from logging */ }
        }
    }
}
