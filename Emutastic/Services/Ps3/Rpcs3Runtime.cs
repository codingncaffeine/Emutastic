using System;
using System.IO;
using System.Linq;

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
        /// True if the emulator has compiled anything to its on-disk cache yet. Used to tailor the
        /// first-launch wait message — a cold cache means a longer one-time optimisation step.
        /// </summary>
        public static bool HasAnyCache()
        {
            try
            {
                string cache = Path.Combine(GetDir(), "cache");
                return Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any();
            }
            catch { return false; }
        }

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

                int scale = App.Configuration?.GetEmulatorConfiguration().Ps3ResolutionScale ?? 100;
                if (scale < 100) scale = 100;

                if (File.Exists(cfg))
                {
                    string y = File.ReadAllText(cfg);
                    y = y.Replace("Multithreaded RSX: false", "Multithreaded RSX: true");
                    y = y.Replace("Start games in fullscreen mode: true", "Start games in fullscreen mode: false");
                    y = System.Text.RegularExpressions.Regex.Replace(y, @"Resolution Scale: \d+", $"Resolution Scale: {scale}");
                    File.WriteAllText(cfg, y);
                }
                else
                {
                    // No config generated yet (fresh install). Write only the keys we need;
                    // the emulator fills in defaults for everything else.
                    File.WriteAllText(cfg,
                        "Video:\n  Multithreaded RSX: true\n  Resolution Scale: " + scale + "\n" +
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

            EnsureInputConfig();
        }

        /// <summary>
        /// Writes a default controller mapping (a standard gamepad bound to player one) if the user
        /// has none. The emulator's settings UI is unavailable when launched without its own UI, so
        /// without this a controller is unbound and nothing responds. Never overwrites an existing
        /// config, so a user who set up their own pad keeps it.
        /// </summary>
        private static void EnsureInputConfig()
        {
            try
            {
                string dir = Path.Combine(GetDir(), "config", "input_configs", "global");
                string file = Path.Combine(dir, "Default.yml");
                if (File.Exists(file))
                {
                    // Self-heal an earlier config where the device name lost its "#1" suffix to a
                    // YAML comment — the value must be quoted because of the '#'.
                    string existing = File.ReadAllText(file);
                    if (existing.Contains("Device: XInput Pad #1"))
                        File.WriteAllText(file, existing.Replace("Device: XInput Pad #1", "Device: \"XInput Pad #1\""));
                    return;
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, DefaultInputConfig);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Ps3] input config: {ex.Message}"); }
        }

        // Player one bound to the standard gamepad handler; the values are the emulator's own
        // built-in defaults, so a controller works out of the box. Other players default to none.
        private const string DefaultInputConfig =
            """
            Player 1 Input:
              Handler: XInput
              Device: "XInput Pad #1"
              Config:
                Left Stick Left: LS X-
                Left Stick Down: LS Y-
                Left Stick Right: LS X+
                Left Stick Up: LS Y+
                Right Stick Left: RS X-
                Right Stick Down: RS Y-
                Right Stick Right: RS X+
                Right Stick Up: RS Y+
                Start: Start
                Select: Back
                PS Button: "Guide,Back&Start"
                Square: X
                Cross: A
                Circle: B
                Triangle: Y
                Left: Left
                Down: Down
                Right: Right
                Up: Up
                R1: RB
                R2: RT
                R3: RS
                L1: LB
                L2: LT
                L3: LS
                IR Nose: ""
                IR Tail: ""
                IR Left: ""
                IR Right: ""
                Tilt Left: ""
                Tilt Right: ""
                Pressure Intensity Button: ""
                Pressure Intensity Percent: 50
                Pressure Intensity Toggle Mode: false
                Pressure Intensity Deadzone: 0
                Analog Limiter Button: ""
                Analog Limiter Toggle Mode: false
                Left Stick Multiplier: 100
                Right Stick Multiplier: 100
                Left Stick Deadzone: 7849
                Right Stick Deadzone: 8689
                Left Stick Anti-Deadzone: 4259
                Right Stick Anti-Deadzone: 4259
                Left Pad Squircling Factor: 8000
                Right Pad Squircling Factor: 8000
                Left Trigger Threshold: 30
                Right Trigger Threshold: 30
                Color Value R: 0
                Color Value G: 0
                Color Value B: 0
                Blink LED when battery is below 20%: true
                Use LED as a battery indicator: false
                LED battery indicator brightness: 50
                Player LED enabled: true
                Large Vibration Motor Multiplier: 100
                Small Vibration Motor Multiplier: 100
                Switch Vibration Motors: false
                Mouse Movement Mode: relative
                Mouse Deadzone X Axis: 60
                Mouse Deadzone Y Axis: 60
                Mouse Acceleration X Axis: 200
                Mouse Acceleration Y Axis: 250
                Left Stick Lerp Factor: 100
                Right Stick Lerp Factor: 100
                Analog Button Lerp Factor: 100
                Trigger Lerp Factor: 100
                Device Class Type: 0
                Vendor ID: 0
                Product ID: 0
              Buddy Device: ""
            """;
    }
}
