using System;
using System.IO;

namespace Emutastic.Services.Ps3
{
    /// <summary>
    /// Locates the external PlayStation 3 emulator package and prepares its configuration
    /// for embedding. The package is acquired on demand through the Cores / Extras downloader
    /// (an app package, not a libretro DLL) and is never bundled with the application.
    /// </summary>
    public static class Rpcs3Runtime
    {
        /// <summary>
        /// Folder that holds the emulator package. Normally a subfolder of the Cores folder;
        /// a development override (EMUTASTIC_RPCS3_DIR) lets a local build be pointed at instead.
        /// </summary>
        public static string GetDir()
        {
            string? dev = Environment.GetEnvironmentVariable("EMUTASTIC_RPCS3_DIR");
            if (!string.IsNullOrWhiteSpace(dev)) return dev;
            return Path.Combine(AppPaths.GetCoresFolder(), "rpcs3");
        }

        public static string GetExe() => Path.Combine(GetDir(), "rpcs3.exe");

        public static bool IsInstalled() => File.Exists(GetExe());

        /// <summary>
        /// Ensures the emulator outputs to a normal, embeddable window (not fullscreen) with the
        /// multi-threaded renderer enabled, and attempts to suppress the one-time first-run notice
        /// so launches aren't gated on it. Idempotent; safe to call before every launch.
        /// </summary>
        public static void PrepareForEmbedding()
        {
            try
            {
                string cfgDir = Path.Combine(GetDir(), "config");
                Directory.CreateDirectory(cfgDir);
                string cfg = Path.Combine(cfgDir, "config.yml");

                if (File.Exists(cfg))
                {
                    string y = File.ReadAllText(cfg);
                    y = y.Replace("Multithreaded RSX: false", "Multithreaded RSX: true");
                    y = y.Replace("Start games in fullscreen mode: true", "Start games in fullscreen mode: false");
                    File.WriteAllText(cfg, y);
                }
                else
                {
                    // No config generated yet (fresh install). Write only the keys we need;
                    // the emulator fills in defaults for everything else.
                    File.WriteAllText(cfg,
                        "Video:\n  Multithreaded RSX: true\n" +
                        "Miscellaneous:\n  Start games in fullscreen mode: false\n");
                }
            }
            catch { /* best effort — a launch still works at emulator defaults */ }

            // Best-effort suppression of the first-run notice. Exact flag to be confirmed
            // against the emulator's settings schema; harmless if ignored.
            try
            {
                string gui = Path.Combine(GetDir(), "GuiConfigs", "CurrentSettings.ini");
                if (File.Exists(gui))
                {
                    string ini = File.ReadAllText(gui);
                    if (!ini.Contains("[InfoBoxes]"))
                        File.AppendAllText(gui, Environment.NewLine + "[InfoBoxes]" + Environment.NewLine + "ib_welcome=false" + Environment.NewLine);
                }
            }
            catch { /* best effort */ }
        }
    }
}
