using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Emutastic.Services;

namespace Emutastic.Views
{
    /// <summary>
    /// Adds a RetroAchievements friend by username. Two-step UI: type
    /// username → Lookup (calls API_GetUserProfile, paints a preview card)
    /// → Add Friend (persists via FriendService.AddAsync).
    /// </summary>
    public partial class AddFriendDialog : Window
    {
        /// <summary>Set by the caller before ShowDialog.</summary>
        public FriendService? FriendService { get; set; }

        private FriendService.LookupResult? _pendingPreview;

        public AddFriendDialog()
        {
            InitializeComponent();
            Loaded += (_, __) => UsernameInput.Focus();
        }

        private async void LookupBtn_Click(object sender, RoutedEventArgs e)
        {
            await DoLookup().ConfigureAwait(true);
        }

        private async void UsernameInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await DoLookup().ConfigureAwait(true);
            }
        }

        private async Task DoLookup()
        {
            if (FriendService == null) return;
            string name = (UsernameInput.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowError("Enter a username first.");
                return;
            }

            LookupBtn.IsEnabled = false;
            ErrorText.Visibility = Visibility.Collapsed;
            PreviewCard.Visibility = Visibility.Collapsed;
            AddBtn.IsEnabled = false;

            try
            {
                var result = await FriendService.LookupAsync(name).ConfigureAwait(true);
                if (!result.Success)
                {
                    ShowError(result.Error ?? "Lookup failed.");
                    return;
                }

                _pendingPreview = result;
                PreviewName.Text = result.Username;
                PreviewPoints.Text =
                    $"{result.PointsHardcore:N0} pts · {result.PointsSoftcore:N0} softcore";
                PreviewMotto.Text = string.IsNullOrWhiteSpace(result.Motto)
                    ? "(no motto set)" : result.Motto;
                if (!string.IsNullOrEmpty(result.AvatarUrl))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 96;
                        bmp.UriSource = new Uri(result.AvatarUrl, UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        PreviewAvatar.Source = bmp;
                    }
                    catch { PreviewAvatar.Source = null; }
                }
                else PreviewAvatar.Source = null;

                PreviewCard.Visibility = Visibility.Visible;
                AddBtn.IsEnabled = true;
            }
            finally { LookupBtn.IsEnabled = true; }
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            if (FriendService == null || _pendingPreview == null) return;
            AddBtn.IsEnabled = false;
            try
            {
                bool added = await FriendService.AddAsync(_pendingPreview).ConfigureAwait(true);
                if (!added)
                {
                    ShowError("That friend is already on your list.");
                    AddBtn.IsEnabled = true;
                    return;
                }
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                AddBtn.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
