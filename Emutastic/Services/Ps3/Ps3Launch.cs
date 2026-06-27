using System.Windows;

namespace Emutastic.Services.Ps3
{
    /// <summary>
    /// Pre-launch readiness gate for PlayStation 3 titles. Ensures the external emulator and the
    /// user-provided system firmware are both present, prompting to install firmware when it's
    /// missing. Returns true only when a launch can proceed.
    /// </summary>
    public static class Ps3Launch
    {
        public static bool EnsureReady(Window? owner)
        {
            if (!Rpcs3Runtime.IsInstalled())
            {
                Show(owner, "PlayStation 3 support isn't installed yet.\n\nInstall it from the " +
                            "Cores / Extras tab, then try again.");
                return false;
            }

            if (!Rpcs3Firmware.IsInstalled())
            {
                var prompt = new Views.Ps3FirmwareWindow();
                if (owner != null) prompt.Owner = owner;
                prompt.ShowDialog();
                return Rpcs3Firmware.IsInstalled();
            }

            return true;
        }

        private static void Show(Window? owner, string message)
        {
            const string caption = "PlayStation 3";
            if (owner != null)
                MessageBox.Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
