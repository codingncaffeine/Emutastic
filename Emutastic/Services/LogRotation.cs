using System;
using System.IO;

namespace Emutastic.Services
{
    /// <summary>
    /// Shared rotation helper for append-only log files. Call
    /// <see cref="RotateIfLarge"/> BEFORE <c>File.AppendAllText</c> on any
    /// log that grows unbounded — when the file passes the cap, it's
    /// renamed to <c>{path}.1</c> (overwriting any prior .1) and the next
    /// append starts a fresh file.
    ///
    /// Idempotent and silent on failure — never throws back to the caller,
    /// so a bad-disk / permissions failure doesn't take down logging
    /// (worst case: log just keeps growing).
    /// </summary>
    internal static class LogRotation
    {
        /// <summary>Default rotation threshold — 2 MB. Override per call site if needed.</summary>
        public const long DefaultCapBytes = 2L * 1024 * 1024;

        public static void RotateIfLarge(string path, long capBytes = DefaultCapBytes)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < capBytes) return;
                string old = path + ".1";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(path, old);
            }
            catch { /* diagnostic — never throw */ }
        }
    }
}
