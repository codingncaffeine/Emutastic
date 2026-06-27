using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Emutastic.Services.Ps3
{
    /// <summary>
    /// Detects and installs the user-provided PlayStation 3 system firmware that the external
    /// emulator needs. The firmware is never bundled; the user supplies their own update file.
    /// Install is driven headlessly through the emulator's own installer.
    /// </summary>
    public static class Rpcs3Firmware
    {
        // The emulator writes a version marker here once firmware is installed.
        private static string VersionFile =>
            Path.Combine(Rpcs3Runtime.GetDir(), "dev_flash", "vsh", "etc", "version.txt");

        public static bool IsInstalled() => File.Exists(VersionFile);

        /// <summary>Installed firmware version (e.g. "4.91"), or null if not installed.</summary>
        public static string? GetVersion()
        {
            try
            {
                if (!File.Exists(VersionFile)) return null;
                foreach (string line in File.ReadLines(VersionFile))
                {
                    if (!line.StartsWith("release:", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = line.Split(':');
                    if (parts.Length < 2) return null;
                    // Marker reads like "04.9100"; present it as "4.91".
                    string raw = parts[1].Trim();
                    if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double v))
                        return (v / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    return raw;
                }
            }
            catch { /* fall through */ }
            return null;
        }

        /// <summary>
        /// Installs firmware from a user-provided update file, headlessly. Returns true if the
        /// firmware is present afterwards. The emulator exits with a non-zero status on headless
        /// shutdown even on success, so the result is verified by the version marker, not the code.
        /// </summary>
        public static async Task<bool> InstallAsync(string firmwarePath)
        {
            try
            {
                if (!Rpcs3Runtime.IsInstalled() || string.IsNullOrEmpty(firmwarePath) || !File.Exists(firmwarePath))
                    return false;

                var psi = new ProcessStartInfo
                {
                    FileName = Rpcs3Runtime.GetExe(),
                    UseShellExecute = false,
                    WorkingDirectory = Rpcs3Runtime.GetDir(),
                };
                psi.ArgumentList.Add("--installfw");
                psi.ArgumentList.Add(firmwarePath);
                psi.ArgumentList.Add("--headless");

                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();
            }
            catch (Exception ex) { Trace.WriteLine($"[Ps3Firmware] install failed: {ex.Message}"); }

            return IsInstalled();
        }
    }
}
