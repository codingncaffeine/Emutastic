using System.Windows;
using Emutastic.Services.Ps3;

namespace Emutastic.Views
{
    /// <summary>
    /// Prompts the user to supply their own PlayStation 3 system firmware and installs it.
    /// The firmware is never bundled. Closes with <see cref="Window.DialogResult"/> true once
    /// firmware is present so the caller can proceed.
    /// </summary>
    public partial class Ps3FirmwareWindow : Window
    {
        public Ps3FirmwareWindow()
        {
            InitializeComponent();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private async void Choose_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select PlayStation 3 firmware",
                Filter = "Firmware update (*.PUP)|*.PUP|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;

            // Firmware installs through the emulator, so it has to be present first. Report that
            // distinctly — otherwise a missing emulator looks like a bad firmware file.
            if (!Rpcs3Runtime.IsInstalled())
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "The PlayStation 3 emulator isn't installed yet. Install it from the Cores / Extras tab, then try again.";
                return;
            }

            ChooseButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "Installing firmware… this can take a minute.";

            bool ok = await Rpcs3Firmware.InstallAsync(dlg.FileName);

            if (ok)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "That file couldn't be installed. Make sure it's an official PS3 firmware update file (PS3UPDAT.PUP) and try again.";
                ChooseButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
            }
        }
    }
}
