using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views
{
    /// <summary>
    /// Library-side cheats manager — opened from the game detail card's ⋯ menu.
    /// Edits the same per-game cheats JSON as the in-game overlay; takes effect
    /// next launch (no live retro_cheat_set without a loaded core).
    /// </summary>
    public partial class CheatsManagerWindow : Window
    {
        private readonly Game _game;
        private readonly string _formatHintCorePath;
        private List<Cheat> _cheats;

        public CheatsManagerWindow(Game game)
        {
            InitializeComponent();

            _game = game;
            _cheats = CheatService.Load(game);

            // No core loaded here — pick the first preferred core for this console
            // so the Add Cheat dialog can still show a sensible format hint.
            _formatHintCorePath = "";
            if (CoreManager.ConsoleCoreMap.TryGetValue(game.Console ?? "", out var cores) && cores.Length > 0)
                _formatHintCorePath = cores[0];

            HeaderTitle.Text = $"Cheats — {game.Title}";
            HeaderSubtitle.Text = "Cheats apply the next time you launch this game.";

            Refresh();
        }

        private void Refresh()
        {
            CheatList.Children.Clear();
            EmptyHint.Visibility = _cheats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < _cheats.Count; i++)
            {
                var cheat = _cheats[i];
                int captured = i;

                var btn = new Button { Style = (Style)FindResource("RowBtn") };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var check = new TextBlock
                {
                    Text              = cheat.Enabled ? "✓" : "",
                    FontSize           = 14,
                    Foreground         = (Brush)FindResource("AccentBrush"),
                    VerticalAlignment  = VerticalAlignment.Center,
                };
                var label = new TextBlock
                {
                    Text              = cheat.Title,
                    Foreground        = cheat.Enabled
                        ? (Brush)FindResource("TextPrimaryBrush")
                        : (Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                };
                var code = new TextBlock
                {
                    Text              = cheat.Code,
                    FontFamily        = new FontFamily("Consolas"),
                    FontSize           = 11,
                    Foreground         = (Brush)FindResource("TextMutedBrush"),
                    VerticalAlignment  = VerticalAlignment.Center,
                    Margin             = new Thickness(8, 0, 0, 0),
                    TextTrimming       = TextTrimming.CharacterEllipsis,
                    MaxWidth           = 140,
                };

                Grid.SetColumn(check, 0);
                Grid.SetColumn(label, 1);
                Grid.SetColumn(code, 2);
                grid.Children.Add(check);
                grid.Children.Add(label);
                grid.Children.Add(code);
                btn.Content = grid;
                btn.Click += (_, _) => OpenEditor(captured);

                CheatList.Children.Add(btn);
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e) => OpenEditor(-1);

        private void OpenEditor(int existingIndex)
        {
            Cheat? existing = (existingIndex >= 0 && existingIndex < _cheats.Count) ? _cheats[existingIndex] : null;
            var dlg = new CheatEditWindow(existing, _formatHintCorePath) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            if (dlg.DeleteRequested && existingIndex >= 0)
                _cheats.RemoveAt(existingIndex);
            else if (existingIndex >= 0)
                _cheats[existingIndex] = dlg.Result;
            else
                _cheats.Add(dlg.Result);

            CheatService.Save(_game, _cheats);
            Refresh();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
