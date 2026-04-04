using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Emutastic.Views
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string message, string confirmLabel = "Delete", bool danger = true)
        {
            InitializeComponent();
            TitleText.Text   = title;
            MessageText.Text = message;
            ConfirmBtn.Content = confirmLabel;

            if (danger)
                ConfirmBtn.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#E03535")!);

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) DialogResult = false;
                if (e.Key == Key.Enter)  DialogResult = true;
            };
        }

        private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e)  => DialogResult = false;
    }
}
