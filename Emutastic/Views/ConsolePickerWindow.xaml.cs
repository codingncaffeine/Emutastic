using System.Windows;
using System.Windows.Controls;

namespace Emutastic.Views
{
    public partial class ConsolePickerWindow : Window
    {
        public string? SelectedConsole { get; private set; }

        // Display-friendly names for the picker buttons
        private static readonly System.Collections.Generic.Dictionary<string, string> Labels = new()
        {
            { "SegaCD",    "Sega CD"               },
            { "Saturn",    "Sega Saturn"            },
            { "PS1",       "PlayStation"            },
            { "TGCD",      "TurboGrafx-CD"          },
            { "PSP",       "PlayStation Portable"   },
            { "GameCube",  "Nintendo GameCube"      },
            { "3DO",       "3DO"                    },
        };

        public ConsolePickerWindow(string fileName, string[] candidates)
        {
            InitializeComponent();
            FileNameText.Text =
                $"\"{fileName}\" wasn't found in any known game database. " +
                $"Which system is it for?";

            // Build display label → console tag pairs for the ItemsControl
            var items = new System.Collections.ObjectModel.ObservableCollection<ConsoleChoice>();
            foreach (string tag in candidates)
                items.Add(new ConsoleChoice(tag, Labels.TryGetValue(tag, out string? lbl) ? lbl : tag));

            ConsoleList.ItemsSource = items;
        }

        private void ConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SelectedConsole = tag;
                DialogResult = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }

    public record ConsoleChoice(string Tag, string Label);
}
