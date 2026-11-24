using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views
{
    /// <summary>
    /// Makes a Commodore 64 hard drive (a CMD HD) for one game: the user picks a size, the core prepares the drive
    /// (its own DOS installed and partitions formatted, by the drive's own programs) off the UI thread with progress
    /// shown here. After ShowDialog, <see cref="Result"/> is a line for the status bar, or null if nothing was made.
    /// </summary>
    public partial class HardDriveWindow : Window
    {
        private readonly Game _game;
        private readonly string _corePath;
        private readonly string _path;
        private readonly CancellationTokenSource _cancel = new();
        private bool _busy;

        public string? Result { get; private set; }

        public HardDriveWindow(Game game, string corePath)
        {
            InitializeComponent();
            _game = game;
            _corePath = corePath;
            _path = C64HardDrive.PathFor(game);
            InfoText.Text = $"A CMD HD hard drive for \"{game.Title}\": device 12, ready to use (SAVE\"NAME\",12 and " +
                            "LOAD\"NAME\",12 from BASIC; LOAD\"$\",12 lists it). It is set up with the drive's own programs, " +
                            "in 16 MB partitions (the largest the drive has). Hard drives are not part of the GitHub backup.";
            foreach (var (label, _) in C64HardDrive.Sizes) SizeBox.Items.Add(label);
            SizeBox.SelectedIndex = 0;

            string? missing = File.Exists(corePath) ? C64HardDrive.Missing(corePath) : null;
            if (missing == null) Warn("The Commodore 64 core is not installed, or this version of it cannot make hard drives.", block: true);
            else if (missing.Length > 0) Warn($"Missing in System Files: {missing}. Add them in Preferences → System Files.", block: true);
            else if (File.Exists(_path)) Warn("This game has a hard drive already: making a new one replaces it and everything on it.", block: false);
            Closing += (_, _) => { if (_busy) _cancel.Cancel(); };
        }

        private void Warn(string text, bool block)
        {
            WarnText.Text = text;
            WarnText.Visibility = Visibility.Visible;
            if (block) OkButton.IsEnabled = false;
        }

        private long SelectedBytes => C64HardDrive.Sizes[Math.Max(0, SizeBox.SelectedIndex)].Bytes;

        private void SizeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SizeNote == null) return;
            int parts = (int)Math.Ceiling((SelectedBytes - 528 * 512.0) / 16_711_680.0);
            SizeNote.Text = $"{parts} partition{(parts == 1 ? "" : "s")}.";
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            OkButton.IsEnabled = false;
            SizeBox.IsEnabled = false;
            Progress.Visibility = StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "Preparing the drive: installing its DOS, then making and formatting the partitions…";
            long bytes = SelectedBytes;
            string label = C64HardDrive.Sizes[SizeBox.SelectedIndex].Label;
            string tmp = _path + ".new";
            try
            {
                int parts = await Task.Run(() =>
                {
                    try { File.Delete(tmp); } catch { }
                    int n = C64HardDrive.Make(_corePath, tmp, bytes, p => Dispatcher.BeginInvoke(() => Progress.Value = p), _cancel.Token);
                    File.Move(tmp, _path, overwrite: true);
                    return n;
                });
                Result = $"Made a {label} hard drive for \"{_game.Title}\" ({parts} partition{(parts == 1 ? "" : "s")}, device 12).";
                _busy = false;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                _busy = false;
                if (_cancel.IsCancellationRequested) { DialogResult = false; return; }
                StatusText.Text = "The drive was not made: " + ex.Message;
                Progress.Visibility = Visibility.Collapsed;
                OkButton.IsEnabled = SizeBox.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) { _cancel.Cancel(); StatusText.Text = "Cancelling…"; }
            else DialogResult = false;
        }
    }
}
