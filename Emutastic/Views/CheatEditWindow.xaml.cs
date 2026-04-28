using System.Windows;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views
{
    public partial class CheatEditWindow : Window
    {
        public Cheat Result { get; private set; } = new();
        public bool DeleteRequested { get; private set; }

        /// <summary>
        /// Opens the dialog. Pass an existing cheat to edit; pass null for Add.
        /// corePath drives the format hint shown under the Code field.
        /// </summary>
        public CheatEditWindow(Cheat? existing, string corePath)
        {
            InitializeComponent();

            var info = CheatSupport.Lookup(corePath);
            if (!string.IsNullOrEmpty(info.FormatHint))
                FormatHint.Text = $"Format: {info.FormatHint}" +
                    (string.IsNullOrEmpty(info.Example) ? "" : $"   e.g. {info.Example}");
            if (!string.IsNullOrEmpty(info.Example))
                CodeBox.Tag = info.Example;

            if (existing != null)
            {
                Title = "Edit Cheat";
                HeaderTitle.Text = "Edit Cheat";
                TitleBox.Text = existing.Title;
                CodeBox.Text = existing.Code;
                EnabledCheck.IsChecked = existing.Enabled;
                SaveBtn.Content = "Save";
                DeleteBtn.Visibility = Visibility.Visible;
            }
            else
            {
                Title = "Add Cheat";
                HeaderTitle.Text = "Add Cheat";
                EnabledCheck.IsChecked = true;
            }

            Loaded += (_, _) => TitleBox.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TitleBox.Text) || string.IsNullOrWhiteSpace(CodeBox.Text))
            {
                MessageBox.Show(this, "Title and Code are both required.", "Cheat",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new Cheat
            {
                Title   = TitleBox.Text.Trim(),
                Code    = CodeBox.Text.Trim(),
                Enabled = EnabledCheck.IsChecked == true,
            };
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            DeleteRequested = true;
            DialogResult = true;
            Close();
        }
    }
}
