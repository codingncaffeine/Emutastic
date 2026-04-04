using Emutastic.Models;
using Emutastic.Services;
using Emutastic.Views;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Emutastic.Views
{
    public partial class GameDetailWindow : Window
    {
        private Game _game;

        public GameDetailWindow(Game game)
        {
            InitializeComponent();
            _game = game;
            PopulateData();
            AnimateIn();
        }

        private void PopulateData()
        {
            GameTitle.Text = _game.Title;
            GameYear.Text = _game.Year > 0 ? _game.Year.ToString() : "";
            ConsoleTag.Text = _game.Console;
            ArtPlaceholderText.Text = _game.Title;
            StatPlayed.Text = _game.PlayCount.ToString();
            StatSaves.Text = _game.SaveCount.ToString();
            StatLastPlayed.Text = _game.LastPlayedDisplay;
            FavoriteBadge.Visibility = _game.IsFavorite
                ? Visibility.Visible
                : Visibility.Collapsed;
            FavoriteButton.Content = _game.IsFavorite ? "♥  Favorited" : "♡  Favorite";

            // Set art background color
            if (System.Windows.Media.ColorConverter.ConvertFromString(_game.BackgroundColor)
                is System.Windows.Media.Color color)
            {
                ArtBgBrush.Color = color;
            }
        }

        private void AnimateIn()
        {
            ModalCard.RenderTransform = new TranslateTransform(0, 30);
            ModalCard.Opacity = 0;

            var slideUp = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));

            ModalCard.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
            ModalCard.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void Overlay_Click(object sender, MouseButtonEventArgs e) => Close();
        private void CloseButton_Click(object sender, MouseButtonEventArgs e) => Close();

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            var coreManager = new CoreManager(App.Configuration);

            // Check for missing BIOS before attempting to launch.
            // Pass the game's region so the check is specific — having a Japan BIOS
            // shouldn't suppress the warning when launching a USA game.
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string systemDir = System.IO.Path.Combine(appData, "OpenEmuWindows", "System");
            string region = RomService.DetectRegion(_game.RomPath);
            string? romDir = System.IO.Path.GetDirectoryName(_game.RomPath);
            var missingBios = CoreManager.GetMissingBios(_game.Console, systemDir, region,
                romDir != null ? new[] { romDir } : null);
            if (missingBios.Count > 0)
            {
                var biosDialog = new BiosRequiredWindow(_game.Console, missingBios, region)
                    { Owner = this };
                biosDialog.ShowDialog();
                return;
            }

            if (!coreManager.HasCore(_game.Console))
            {
                MessageBox.Show(
                    $"No emulator core found for {_game.Console}.\n\nMake sure the appropriate .dll core file is in the Cores folder next to the application.",
                    "Missing Core",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!System.IO.File.Exists(_game.RomPath))
            {
                MessageBox.Show(
                    $"ROM file not found:\n{_game.RomPath}",
                    "File Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine("Creating emulator for " + _game.Console);
                string corePath = coreManager.GetCorePath(_game.Console)!;
                System.Diagnostics.Debug.WriteLine("Core path: " + corePath);
                
                var core = new LibretroCore(corePath);
                System.Diagnostics.Debug.WriteLine("LibretroCore created successfully");
                
                var emulator = new EmulatorWindow(_game, core);
                System.Diagnostics.Debug.WriteLine("EmulatorWindow created successfully");
                
                emulator.ShowDialog();
                System.Diagnostics.Debug.WriteLine("EmulatorWindow closed");
                Close(); // Close this window after emulator is done
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to launch emulator:\n\n{ex.Message}",
                    "Launch Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            _game.IsFavorite = !_game.IsFavorite;
            FavoriteButton.Content = _game.IsFavorite ? "♥  Favorited" : "♡  Favorite";
            FavoriteBadge.Visibility = _game.IsFavorite
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            // Context menu — file info, delete, etc.
        }
    }
}