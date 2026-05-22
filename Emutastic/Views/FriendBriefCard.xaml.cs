using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Emutastic.Configuration;
using Emutastic.Services;

namespace Emutastic.Views
{
    /// <summary>
    /// Compact popup shown when a friend row is clicked on the
    /// Achievements → Friends sub-tab. Identity + points + last
    /// activity + two actions (Open Full Profile, Remove).
    ///
    /// Dismiss model mirrors GameDetail: MainWindow.OnPreviewMouseDown
    /// closes it. The card calls Show() (not ShowDialog) so the parent
    /// remains interactive — this is required for the dismiss-on-click
    /// pattern to work.
    /// </summary>
    public partial class FriendBriefCard : Window
    {
        private readonly int _userId;
        private bool _toastsEnabled;

        /// <summary>Raised when the user clicks "Open Full Profile".</summary>
        public event EventHandler<int>? OpenProfileRequested;
        /// <summary>Raised when the user clicks "Remove".</summary>
        public event EventHandler<int>? RemoveRequested;

        private readonly FriendService _friends;

        public FriendBriefCard(FriendEntry entry, FriendCacheSnapshot? snap, FriendService friends)
        {
            InitializeComponent();
            _userId = entry.UserId;
            _friends = friends;
            _toastsEnabled = entry.ToastsEnabled;

            // Always set the friend's name from the entry (authoritative
            // identity). The snap can be empty/stale; the name field
            // can't.
            BriefName.Text = entry.Username;

            // Mutual-follow chip and bell-toggle state both derive from the
            // FriendEntry (config-level), not from the cache snapshot.
            BriefMutualChip.Visibility = entry.MutualFollow ? Visibility.Visible : Visibility.Collapsed;
            ApplyToastsIcon();
            BriefToastsToggle.MouseEnter += (_, __) => StartBellHover();
            BriefToastsToggle.MouseLeave += (_, __) => StopBellHover();

            // Re-read snap fresh from the service rather than trusting
            // the parameter, which can be stale if polling rewrote
            // between row paint and click.
            var fresh = _friends.GetSnapshot(_userId) ?? snap;
            ApplySnapshot(fresh);

            // Dismiss model: this card is a separate Window so clicks on
            // it don't bubble to MainWindow. MainWindow.OnPreviewMouseDown
            // (tunneling phase) fires on any click that lands inside
            // MainWindow's surface and calls CloseBrief(). Deactivated
            // doesn't fire on owned windows (see feedback memory) so
            // we don't rely on it.
        }

        private void ApplySnapshot(FriendCacheSnapshot? snap)
        {
            if (snap == null)
            {
                BriefPoints.Text = "Loading…";
                BriefMotto.Text = "";
                BriefLastActivity.Text = "—";
                BriefLastPlayedRow.Visibility = Visibility.Collapsed;
                BriefUnlocks24hCard.Visibility = Visibility.Collapsed;
                System.Diagnostics.Trace.WriteLine("[FriendBriefCard] snap is null — no data to render");
                return;
            }

            System.Diagnostics.Trace.WriteLine($"[FriendBriefCard] snap: avatar=[{snap.AvatarUrl}] hc={snap.PointsHardcore} sc={snap.PointsSoftcore} motto=[{snap.Motto}] lastGame=[{snap.LastGameTitle}] icon=[{snap.LastGameImageIcon}] 24h={snap.RecentUnlockCount24h}");

            BriefPoints.Text = FriendsCopy.PointsAndSoftcore(snap.PointsHardcore, snap.PointsSoftcore);
            BriefMotto.Text = string.IsNullOrWhiteSpace(snap.Motto) ? "" : snap.Motto;

            if (string.IsNullOrEmpty(snap.LastGameTitle))
            {
                BriefLastPlayedRow.Visibility = Visibility.Collapsed;
            }
            else
            {
                BriefLastPlayedRow.Visibility = Visibility.Visible;
                BriefLastActivity.Text = snap.LastGameTitle;
                if (!string.IsNullOrEmpty(snap.LastGameImageIcon))
                {
                    TryLoadImage(BriefLastGameImage, "https://media.retroachievements.org" + snap.LastGameImageIcon, 80, "game icon");
                }
            }

            if (snap.RecentUnlockCount24h > 0)
            {
                BriefUnlocks24hCard.Visibility = Visibility.Visible;
                BriefUnlocks24hText.Text = snap.RecentUnlockCount24h == 1
                    ? "1 achievement unlocked in the last 24 hours"
                    : $"{snap.RecentUnlockCount24h} achievements unlocked in the last 24 hours";
            }
            else
            {
                BriefUnlocks24hCard.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrEmpty(snap.AvatarUrl))
            {
                TryLoadImage(BriefAvatar, snap.AvatarUrl, 112, "avatar");
            }
            else
            {
                System.Diagnostics.Trace.WriteLine("[FriendBriefCard] avatar URL is empty in snap — skipping load");
            }
        }

        private static void TryLoadImage(System.Windows.Controls.Image target, string url, int decodeWidth, string label)
        {
            // decodeWidth retained for API stability but ignored —
            // FriendImageLoader doesn't set DecodePixelWidth (it races
            // the async HTTPS download). The 56-112px display size is
            // small enough that the full image cost isn't a concern.
            FriendImageLoader.Load(target, url, $"brief-{label}");
        }

        /// <summary>Closes the card if it's still open. Idempotent.</summary>
        public void CloseBrief()
        {
            try { Close(); } catch { }
        }

        private void OpenProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenProfileRequested?.Invoke(this, _userId);
            CloseBrief();
        }

        private void RemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            RemoveRequested?.Invoke(this, _userId);
            CloseBrief();
        }

        private async void BriefToastsToggle_Click(object sender, RoutedEventArgs e)
        {
            // Optimistic UI: flip the icon immediately so the click feels
            // responsive. The async config write reconciles in the
            // background — if it fails the next FriendListChanged event
            // (or a re-open of the card) will resync from the canonical
            // FriendEntry.
            _toastsEnabled = !_toastsEnabled;
            ApplyToastsIcon();
            try
            {
                await _friends.SetToastsEnabledAsync(_userId, _toastsEnabled);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[FriendBriefCard] SetToastsEnabledAsync failed: {ex.Message}");
            }
        }

        private void ApplyToastsIcon()
        {
            BriefToastsIcon.Kind = _toastsEnabled
                ? MaterialDesignThemes.Wpf.PackIconKind.Bell
                : MaterialDesignThemes.Wpf.PackIconKind.BellOff;
            BriefToastsToggle.ToolTip = _toastsEnabled
                ? "Notifications on — click to mute this friend's toasts"
                : "Notifications off — click to enable this friend's toasts";
        }

        private static readonly System.Windows.Media.Color BellHoverColor =
            System.Windows.Media.Color.FromRgb(0xE0, 0xB5, 0x4B);

        private void StartBellHover()
        {
            BriefToastsIcon.Foreground = new System.Windows.Media.SolidColorBrush(BellHoverColor);
            var ring = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = -18,
                To   =  18,
                Duration = TimeSpan.FromMilliseconds(140),
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut,
                },
            };
            BriefToastsBellRotate.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty, ring);
        }

        private void StopBellHover()
        {
            BriefToastsIcon.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            BriefToastsBellRotate.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty, null);
            BriefToastsBellRotate.Angle = 0;
        }
    }
}
