using System;
using System.Diagnostics;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace Emutastic.Services
{
    /// <summary>
    /// Owns the process-wide <see cref="LibVLC"/> instance used by snap-video
    /// previews in the game detail window. The first construction is multi-second
    /// (native init: libvlc.dll/libvlccore.dll load + plugin scan), so this service
    /// kicks the warm-up off the UI thread at app startup. Subsequent callers
    /// await an already-completed Task.
    /// </summary>
    internal sealed class VideoPlaybackService
    {
        public static VideoPlaybackService Instance { get; } = new();

        private Task<LibVLC>? _warmup;
        private readonly object _gate = new();

        private VideoPlaybackService() { }

        public void StartWarmup() => _ = GetLibVLCAsync();

        public Task<LibVLC> GetLibVLCAsync()
        {
            if (_warmup != null) return _warmup;
            lock (_gate)
            {
                _warmup ??= Task.Run(CreateLibVLC);
            }
            return _warmup;
        }

        private static LibVLC CreateLibVLC()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var lib = new LibVLC("--no-audio", "--no-osd", "--no-snapshot-preview");
                sw.Stop();
                Trace.WriteLine($"[VideoPlayback] LibVLC warmed in {sw.ElapsedMilliseconds}ms");
                return lib;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Trace.WriteLine($"[VideoPlayback] LibVLC init FAILED after {sw.ElapsedMilliseconds}ms: {ex.Message}");
                // Surface failures — StartWarmup is fire-and-forget so the faulted
                // Task is otherwise invisible; later snap callers will also see
                // their await throw but get silently swallowed by LoadSnapAsync's
                // outer try, leaving us with no signal at all.
                App.Logger?.LogError(ex, "LibVLC warmup failed — snap video previews will be unavailable");
                throw;
            }
        }
    }
}
