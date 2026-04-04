using System.Windows;
using System.Windows.Input;

namespace Emutastic.Views
{
    public partial class RenameWindow : Window
    {
        public string NewTitle { get; private set; } = "";

        public RenameWindow(string currentTitle)
        {
            InitializeComponent();
            TitleBox.Text = currentTitle;
            TitleBox.SelectAll();
            TitleBox.Focus();
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) return;
            NewTitle = TitleBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;

        private void TitleBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Rename_Click(sender, e);
            if (e.Key == Key.Escape) Cancel_Click(sender, e);
        }
    }
}