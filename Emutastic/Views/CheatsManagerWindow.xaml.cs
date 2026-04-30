using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Pill-style sliding toggle — knob right + accent when on,
                // knob left + muted when off. Reads unambiguously as a control.
                var knob = new Border
                {
                    Width             = 14,
                    Height            = 14,
                    CornerRadius      = new CornerRadius(7),
                    Background        = Brushes.White,
                    Margin            = new Thickness(2, 0, 2, 0),
                    HorizontalAlignment = cheat.Enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                var toggle = new Border
                {
                    Background        = cheat.Enabled
                        ? (Brush)FindResource("AccentBrush")
                        : (Brush)FindResource("BgTertiaryBrush"),
                    BorderBrush       = (Brush)FindResource("BorderNormalBrush"),
                    BorderThickness   = new Thickness(1),
                    Width             = 34,
                    Height            = 18,
                    CornerRadius      = new CornerRadius(9),
                    Cursor            = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip           = cheat.Enabled ? "Click to disable" : "Click to enable",
                    Child             = knob,
                };
                toggle.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;   // prevent the row Button from firing OpenEditor
                    ToggleCheat(captured);
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

                Grid.SetColumn(toggle, 0);
                Grid.SetColumn(label, 1);
                Grid.SetColumn(code, 2);
                grid.Children.Add(toggle);
                grid.Children.Add(label);
                grid.Children.Add(code);
                btn.Content = grid;
                btn.Click += (_, _) => OpenEditor(captured);

                CheatList.Children.Add(btn);
            }
        }

        private void ToggleCheat(int index)
        {
            if (index < 0 || index >= _cheats.Count) return;
            _cheats[index].Enabled = !_cheats[index].Enabled;
            CheatService.Save(_game, _cheats);
            // No live apply here — library-side, no core loaded; takes effect next launch.
            Refresh();
        }

        private void Add_Click(object sender, RoutedEventArgs e) => OpenEditor(-1);

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (!CheatDatabaseService.IsInstalled())
            {
                MessageBox.Show(this,
                    "The cheats database hasn't been downloaded yet.\n\n" +
                    "Open Preferences → Cores / Extras and click Download next to \"Cheats Database\".",
                    "No Database",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = CheatDatabaseService.LookupForGame(_game);
            if (result == null)
            {
                MessageBox.Show(this,
                    "No cheats found in the database for this game.\n\n" +
                    "The database is matched by ROM filename, so renames or non-standard filenames may miss.",
                    "No Cheats Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // If the database had only Action Replay codes for this system and
            // they were all filtered out, tell the user why instead of silently
            // showing "0 imported".
            if (result.Cheats.Count == 0 && result.SkippedActionReplay > 0)
            {
                MessageBox.Show(this,
                    $"The database has {result.SkippedActionReplay} Action Replay code(s) for this game, " +
                    "but Action Replay codes don't apply reliably with the current Genesis core " +
                    "(known issue — codes either do nothing or cause graphical glitches).\n\n" +
                    "Game Genie codes for this game would import normally if the database had any.",
                    "Action Replay Codes Skipped",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Skip duplicates (matched by code, since titles can differ slightly).
            var existingCodes = new HashSet<string>(_cheats.Select(c => c.Code), System.StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var c in result.Cheats)
            {
                if (existingCodes.Contains(c.Code)) continue;
                _cheats.Add(c);
                added++;
            }
            CheatService.Save(_game, _cheats);
            Refresh();

            string msg;
            if (added > 0)
            {
                msg = $"Imported {added} cheat(s) from the database.\nAll are disabled by default — toggle the ones you want.";
                if (result.SkippedActionReplay > 0)
                {
                    msg += $"\n\n{result.SkippedActionReplay} Action Replay code(s) were skipped " +
                           "(known unreliable on this system's core).";
                }
            }
            else
            {
                msg = "All matching cheats from the database are already in your list.";
            }
            MessageBox.Show(this, msg, "Cheats Imported",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

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
