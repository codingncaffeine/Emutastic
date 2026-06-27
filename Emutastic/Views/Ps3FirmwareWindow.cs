using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Emutastic.Services.Ps3;

namespace Emutastic.Views
{
    /// <summary>
    /// Prompts the user to supply their own PlayStation 3 system firmware and installs it.
    /// The firmware is never bundled. Closes with <see cref="Window.DialogResult"/> true once
    /// firmware is present so the caller can proceed to launch.
    /// </summary>
    public sealed class Ps3FirmwareWindow : Window
    {
        private readonly TextBlock _status;
        private readonly Button _choose;

        public Ps3FirmwareWindow()
        {
            Title = "PlayStation 3 Firmware Required";
            Width = 460;
            Height = 230;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21));

            _status = new TextBlock
            {
                Text = "PlayStation 3 games need the system firmware, which is not included.\n\n" +
                       "Select your own firmware update file (PS3UPDAT.PUP) to install it.",
                Foreground = Brushes.White,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
            };

            _choose = new Button
            {
                Content = "Choose firmware file…",
                Padding = new Thickness(14, 6, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _choose.Click += OnChoose;

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(_status);
            panel.Children.Add(_choose);
            Content = panel;
        }

        private async void OnChoose(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select PlayStation 3 firmware",
                Filter = "Firmware update (*.PUP)|*.PUP|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;

            _choose.IsEnabled = false;
            _status.Text = "Installing firmware… this can take a minute.";

            bool ok = await Rpcs3Firmware.InstallAsync(dlg.FileName);

            if (ok)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                _status.Text = "That file couldn't be installed. Make sure it's a valid firmware update file and try again.";
                _choose.IsEnabled = true;
            }
        }
    }
}
