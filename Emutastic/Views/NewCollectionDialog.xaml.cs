using System.Windows;
using System.Windows.Input;

namespace Emutastic.Views
{
    public partial class NewCollectionDialog : Window
    {
        public string CollectionName { get; private set; } = "";

        public NewCollectionDialog()
        {
            InitializeComponent();
            NameBox.Focus();
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) return;
            CollectionName = NameBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)  Create_Click(sender, e);
            if (e.Key == Key.Escape) Cancel_Click(sender, e);
        }
    }
}
