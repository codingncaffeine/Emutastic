using System;
using System.IO;
using System.Windows.Media;
using Emutastic.Configuration;

namespace Emutastic.Services
{
    /// <summary>
    /// Plays the friend-notification sound (Assets/Sounds/Notification1.mp3)
    /// as a fire-and-forget one-shot. Used for LB triumph toasts (Phase 6b)
    /// where the emotional weight warrants audio.
    ///
    /// Uses WPF MediaPlayer (not the libretro PCM-stream AudioPlayer) —
    /// mixes naturally with emulator audio via the Windows audio session.
    /// MediaPlayer requires a live field reference until playback finishes;
    /// the static `_activeSound` holds it. Replacing on a new toast cuts
    /// off any overlapping playback (latest wins; prevents pile-up).
    ///
    /// MediaPlayer rejects pack://application resource URIs ("Only
    /// site-of-origin pack URIs are supported for media."), so the mp3 is
    /// shipped as a loose Content file next to the exe and loaded via
    /// filesystem path.
    /// </summary>
    public static class FriendNotificationSound
    {
        private static readonly Uri _soundUri = new(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "Notification1.mp3"),
            UriKind.Absolute);

        private static MediaPlayer? _activeSound;
        private static readonly object _gate = new();

        /// <summary>
        /// Plays the notification sound at the configured volume. No-op
        /// when <see cref="FriendsConfiguration.LbToastSoundEnabled"/> is
        /// false. Safe to call from the UI thread; non-blocking.
        /// </summary>
        public static void Play(IConfigurationService? config)
        {
            try
            {
                var cfg = config?.GetFriendsConfiguration();
                if (cfg == null || !cfg.LbToastSoundEnabled)
                {
                    RaLog.Write($"[ToastSound] Play SKIPPED (cfgNull={cfg == null}, enabled={cfg?.LbToastSoundEnabled})");
                    return;
                }
                // Hardcoded to full volume — there is no Preferences UI for
                // LbToastSoundVolume so the per-user setting was effectively
                // hidden behind a config-file edit, and 85% defaulted too
                // quiet over emulator audio. Restore the config-driven path
                // (Math.Clamp(cfg.LbToastSoundVolume, 0, 100) / 100.0) when a
                // slider is added to Preferences.
                const double volume = 1.0;
                string path = _soundUri.LocalPath;
                bool exists = File.Exists(path);
                RaLog.Write($"[ToastSound] Play uri=[{_soundUri}] path=[{path}] exists={exists} vol={volume:F2}");
                if (!exists) return;

                lock (_gate)
                {
                    try { _activeSound?.Close(); } catch { }
                    var player = new MediaPlayer();
                    player.MediaOpened += (s, e) =>
                        RaLog.Write($"[ToastSound] MediaOpened ok");
                    player.MediaEnded += (s, e) =>
                    {
                        try { ((MediaPlayer)s!).Close(); } catch { }
                    };
                    player.MediaFailed += (s, e) =>
                        RaLog.Write($"[ToastSound] MediaFailed: {e.ErrorException?.Message}");
                    player.Open(_soundUri);
                    player.Volume = volume;
                    player.Play();
                    _activeSound = player;
                }
            }
            catch (Exception ex)
            {
                RaLog.Write($"[ToastSound] play failed: {ex.Message}");
            }
        }
    }
}
