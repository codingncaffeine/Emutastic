using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emutastic.Configuration;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views
{
    /// <summary>
    /// Full friend profile window. Non-modal — multiple can be open at
    /// once (MainWindow tracks them in a Dictionary&lt;int, FriendDetailWindow&gt;
    /// keyed by friend UserId and focuses the existing instance instead
    /// of opening a duplicate).
    ///
    /// Phase 4 ships Overview + Recent. Compare and Leaderboards inner
    /// tabs are placeholder-only — Phase 5 / Phase 6 fill them in.
    /// </summary>
    public partial class FriendDetailWindow : Window
    {
        public int FriendUserId { get; }

        private readonly FriendService _friends;
        private readonly RetroAchievementsService _api;
        private FriendEntry? _entry;
        private bool _toastsEnabled;

        private readonly DatabaseService _db;
        private List<RAUserCompletionProgressItem>? _mineProgress;
        private List<RAUserCompletionProgressItem>? _theirsProgress;
        private bool _comparePainted;
        private bool _lbPickerPainted;
        // List of GameId values backing LbGamePicker — parallel to its
        // items so the SelectionChanged handler can look up the chosen
        // game's ID without parsing display text.
        private readonly List<int> _lbGameIds = new();

        public FriendDetailWindow(FriendEntry entry, FriendService friends, RetroAchievementsService api, DatabaseService db)
        {
            InitializeComponent();
            FriendUserId = entry.UserId;
            _entry = entry;
            _friends = friends;
            _api = api;
            _db = db;

            Title = $"{entry.Username} — Profile";
            TitleBarText.Text = Title;

            _toastsEnabled = entry.ToastsEnabled;
            ApplyToastsIcon();
            HeaderToastsToggle.MouseEnter += (_, __) => StartBellHover();
            HeaderToastsToggle.MouseLeave += (_, __) => StopBellHover();

            // Stay in sync with the brief card popup — both write through
            // FriendService.SetToastsEnabledAsync which fires FriendListChanged.
            _friends.FriendListChanged += OnFriendListChanged;
            Closed += (_, __) => _friends.FriendListChanged -= OnFriendListChanged;

            Loaded += async (_, __) =>
            {
                PaintFromCache();
                await FetchFreshAsync().ConfigureAwait(true);
            };
            // Lazy-load tab data only when the user opens it.
            DetailTabs.SelectionChanged += async (sender, e) =>
            {
                // TabControl raises SelectionChanged on inner Selectors too
                // (e.g. the Compare sort ComboBox). Guard by checking the
                // originator is the TabControl itself.
                if (!ReferenceEquals(e.OriginalSource, DetailTabs)) return;

                if (DetailTabs.SelectedItem == CompareTab && !_comparePainted)
                {
                    _comparePainted = true;
                    await LoadCompareAsync().ConfigureAwait(true);
                }
                else if (DetailTabs.SelectedItem == LeaderboardsTab && !_lbPickerPainted)
                {
                    // Only mark painted on SUCCESS — if populate fails
                    // (missing username, transient network) the user can
                    // retry by switching tabs and coming back instead of
                    // having to reopen the window.
                    bool ok = await PopulateLbGamePickerAsync().ConfigureAwait(true);
                    if (ok) _lbPickerPainted = true;
                }
            };
        }

        // Themed title-bar: drag to move, X to close. Matches MainWindow
        // chrome (WindowStyle=None + AllowsTransparency=True).
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                e.Handled = true;
                return;
            }
            try { DragMove(); } catch { /* swallow ReentrantDragMove on edge cases */ }
        }

        private void TitleBarCloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // Paint from the SQLite-cached snapshot for an instant header.
        // Background fetch then upgrades to live data.
        private void PaintFromCache()
        {
            if (_entry == null) return;
            var snap = _friends.GetSnapshot(_entry.UserId);
            HeaderUsername.Text = _entry.Username;
            if (snap != null)
            {
                System.Diagnostics.Trace.WriteLine($"[FriendDetail] snap: avatar=[{snap.AvatarUrl}] hc={snap.PointsHardcore} sc={snap.PointsSoftcore} motto=[{snap.Motto}] lastGame=[{snap.LastGameTitle}] icon=[{snap.LastGameImageIcon}]");
                HeaderMotto.Text = string.IsNullOrWhiteSpace(snap.Motto)
                    ? "(no motto set)" : snap.Motto;
                HeaderHardcorePoints.Text = snap.PointsHardcore.ToString("N0");
                HeaderSoftcorePoints.Text = $"{snap.PointsSoftcore:N0} pts";
                HeaderMemberSince.Text = FriendsCopy.MemberSinceDisplay(snap.MemberSince);
                LoadAvatar(snap.AvatarUrl);
                RenderStats(snap);
            }
        }

        /// <summary>
        /// Fetches the friend's last 5 played games and populates the
        /// Recently Played card. Fire-and-forget; failures fall back to
        /// "No recently played games." Empty list (private profile, brand
        /// new account) shows the same fallback. Single HTTP call per
        /// detail-window open — small enough to skip caching.
        /// </summary>
        private async Task FetchRecentlyPlayedAsync()
        {
            if (_entry == null) return;
            try
            {
                var recent = await Task.Run(() =>
                        _api.GetUserRecentlyPlayedGamesAsync(_entry.Username, 5))
                    .ConfigureAwait(true);
                RecentlyPlayedItems.Items.Clear();
                if (recent == null || recent.Count == 0)
                {
                    RecentlyPlayedEmpty.Visibility = Visibility.Visible;
                    return;
                }
                RecentlyPlayedEmpty.Visibility = Visibility.Collapsed;
                foreach (var g in recent)
                    RecentlyPlayedItems.Items.Add(BuildRecentlyPlayedRow(g));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[FriendDetail] recently played fetch failed: {ex.Message}");
                RecentlyPlayedEmpty.Text = "Couldn't load recently played games.";
                RecentlyPlayedEmpty.Visibility = Visibility.Visible;
            }
        }

        private FrameworkElement BuildRecentlyPlayedRow(Models.RARecentlyPlayedGame g)
        {
            var border = new Border
            {
                Padding = new Thickness(0, 6, 0, 6),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Game icon — 40x40 rounded square.
            var iconBorder = new Border
            {
                Width = 40, Height = 40,
                CornerRadius = new CornerRadius(4),
                Background = (System.Windows.Media.Brush)FindResource("BgTertiaryBrush"),
                ClipToBounds = true,
            };
            if (!string.IsNullOrEmpty(g.ImageIcon))
            {
                var img = new System.Windows.Controls.Image { Stretch = System.Windows.Media.Stretch.UniformToFill };
                iconBorder.Child = img;
                string fullUrl = g.ImageIcon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? g.ImageIcon
                    : "https://media.retroachievements.org" + g.ImageIcon;
                FriendImageLoader.Load(img, fullUrl, "recent-game-icon", $"game={g.Title}");
            }
            Grid.SetColumn(iconBorder, 0);

            // Title + console + time-ago.
            var stack = new StackPanel
            {
                Margin = new Thickness(10, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new TextBlock
            {
                Text = g.Title,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var subParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(g.ConsoleName)) subParts.Add(g.ConsoleName);
            string ago = FormatTimeAgo(g.LastPlayed);
            if (!string.IsNullOrEmpty(ago)) subParts.Add(ago);
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", subParts),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(stack, 1);

            // Progress badge (N / M achievements).
            if (g.NumPossibleAchievements > 0)
            {
                var progress = new TextBlock
                {
                    Text = $"{g.NumAchievedHardcore}/{g.NumPossibleAchievements}",
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("PrimaryFont"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(progress, 2);
                grid.Children.Add(progress);
            }

            grid.Children.Add(iconBorder);
            grid.Children.Add(stack);
            border.Child = grid;
            return border;
        }

        /// <summary>
        /// Per-friend toast toggle — same write path as the brief card's bell.
        /// Optimistic UI: flip locally first, then write to config. If the
        /// write fails, OnFriendListChanged (no-op when the values already
        /// agree) leaves the local state alone.
        /// </summary>
        private async void HeaderToastsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_entry == null) return;
            _toastsEnabled = !_toastsEnabled;
            ApplyToastsIcon();
            try
            {
                await _friends.SetToastsEnabledAsync(_entry.UserId, _toastsEnabled);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[FriendDetail] SetToastsEnabledAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Cross-surface sync: when the brief card flips ToastsEnabled on
        /// the same friend, re-read the canonical value from config and
        /// update this window's icon + label so the two surfaces never
        /// disagree.
        /// </summary>
        private void OnFriendListChanged(object? sender, EventArgs e)
        {
            if (_entry == null) return;
            try
            {
                var live = _friends.Friends.FirstOrDefault(f => f.UserId == _entry.UserId);
                if (live == null) return;
                if (live.ToastsEnabled == _toastsEnabled) return; // no change for this friend
                Dispatcher.BeginInvoke(() =>
                {
                    _toastsEnabled = live.ToastsEnabled;
                    ApplyToastsIcon();
                });
            }
            catch { }
        }

        private void ApplyToastsIcon()
        {
            HeaderToastsIcon.Kind = _toastsEnabled
                ? MaterialDesignThemes.Wpf.PackIconKind.Bell
                : MaterialDesignThemes.Wpf.PackIconKind.BellOff;
            HeaderToastsLabel.Text = _toastsEnabled
                ? "Notifications on — click to mute this friend"
                : "Notifications off — click to enable notifications";
            HeaderToastsToggle.ToolTip = HeaderToastsLabel.Text;
        }

        // Muted gold that reads well against the dark theme; saturated gold
        // (#FFD700) is too garish next to our accent red.
        private static readonly System.Windows.Media.Color BellHoverColor =
            System.Windows.Media.Color.FromRgb(0xE0, 0xB5, 0x4B);

        private void StartBellHover()
        {
            // Foreground swap: hold a reference to the original brush so we
            // can restore it exactly (preserves whatever theme it resolved to).
            HeaderToastsIcon.Foreground = new System.Windows.Media.SolidColorBrush(BellHoverColor);

            // Ring animation: small back-and-forth pivot at the top of the
            // bell (RenderTransformOrigin 0.5,0.15 in the XAML).
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
            HeaderToastsBellRotate.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty, ring);
        }

        private void StopBellHover()
        {
            HeaderToastsIcon.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            // Stopping the animation by passing null leaves the angle at its
            // last animated value, so explicitly reset to upright.
            HeaderToastsBellRotate.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty, null);
            HeaderToastsBellRotate.Angle = 0;
        }

        private static string FormatTimeAgo(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return "";
            if (!DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dt))
                return "";
            var delta = DateTime.UtcNow - dt.ToUniversalTime();
            if (delta.TotalMinutes < 1)  return "just now";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
            if (delta.TotalHours   < 24) return $"{(int)delta.TotalHours}h ago";
            if (delta.TotalDays    < 30) return $"{(int)delta.TotalDays}d ago";
            return dt.ToString("yyyy-MM-dd");
        }

        // Pull fresh profile + recent unlocks. Errors fall back to cache.
        private async Task FetchFreshAsync()
        {
            if (_entry == null) return;
            try
            {
                var profile = await _api.GetUserProfileAsync(_entry.Username).ConfigureAwait(true);
                if (profile != null)
                {
                    HeaderUsername.Text = string.IsNullOrEmpty(profile.User) ? _entry.Username : profile.User;
                    HeaderMotto.Text = string.IsNullOrWhiteSpace(profile.Motto)
                        ? "(no motto set)" : profile.Motto!;
                    HeaderHardcorePoints.Text = profile.TotalPoints.ToString("N0");
                    HeaderSoftcorePoints.Text = $"{profile.TotalSoftcorePoints:N0} pts";
                    HeaderMemberSince.Text = FriendsCopy.MemberSinceDisplay(profile.MemberSince ?? "");
                    LoadAvatar("https://media.retroachievements.org" + (profile.UserPic ?? ""));

                    var prevSnap = _friends.GetSnapshot(_entry.UserId);
                    RenderStats(new FriendCacheSnapshot
                    {
                        PointsHardcore = profile.TotalPoints,
                        PointsSoftcore = profile.TotalSoftcorePoints,
                        TruePoints     = profile.TotalTruePoints,
                        LastGameId     = profile.LastGameId,
                        LastGameTitle  = prevSnap?.LastGameTitle ?? "",
                    });
                }

                // Fire the recently-played fetch in parallel with the
                // recent-unlocks fetch below — both hit the network and
                // don't depend on each other.
                _ = FetchRecentlyPlayedAsync();
            }
            catch (Exception ex)
            {
                OverviewStatusText.Text = $"Couldn't refresh profile: {ex.Message}";
                OverviewStatusText.Visibility = Visibility.Visible;
            }

            try
            {
                // 24h lookback so the Recent tab shows more than the
                // narrow polling-window slice. RA caps at 1440 min.
                var recent = await _api.GetUserRecentAchievementsAsync(_entry.Username, minutes: 1440)
                                       .ConfigureAwait(true);
                RenderRecent(recent);
            }
            catch (Exception ex)
            {
                RecentEmptyText.Text = $"Couldn't load recent activity: {ex.Message}";
                RecentEmptyText.Visibility = Visibility.Visible;
            }

            // Now that we have fresh data, the activity feed has been
            // viewed implicitly — reset the unread badge.
            _friends.MarkSeen(_entry.UserId);
        }

        private void LoadAvatar(string url)
        {
            FriendImageLoader.Load(HeaderAvatar, url, "detail-avatar", $"user={_entry?.Username}");
        }

        private void RenderStats(FriendCacheSnapshot snap)
        {
            StatsGrid.Children.Clear();
            StatsGrid.Children.Add(BuildStatCell("HARDCORE PTS", snap.PointsHardcore.ToString("N0")));
            StatsGrid.Children.Add(BuildStatCell("SOFTCORE PTS", snap.PointsSoftcore.ToString("N0")));
            StatsGrid.Children.Add(BuildStatCell("TRUE PTS",     snap.TruePoints.ToString("N0")));
            string lastPlayed = string.IsNullOrEmpty(snap.LastGameTitle) ? "—" : snap.LastGameTitle;
            StatsGrid.Children.Add(BuildStatCell("LAST PLAYED",  lastPlayed));
        }

        private FrameworkElement BuildStatCell(string label, string value)
        {
            var stack = new StackPanel
            {
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            stack.Children.Add(new TextBlock
            {
                Text = value,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            return stack;
        }

        private void RenderRecent(System.Collections.Generic.List<RAUserRecentAchievement>? recent)
        {
            RecentUnlocksList.Items.Clear();
            if (recent == null || recent.Count == 0)
            {
                RecentEmptyText.Visibility = Visibility.Visible;
                return;
            }
            RecentEmptyText.Visibility = Visibility.Collapsed;

            foreach (var u in recent.Take(50))
                RecentUnlocksList.Items.Add(BuildRecentRow(u));
        }

        private FrameworkElement BuildRecentRow(RAUserRecentAchievement u)
        {
            var border = new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 40, Height = 40,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("BgTertiaryBrush"),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!string.IsNullOrEmpty(u.BadgeName))
            {
                var img = new Image { Stretch = Stretch.UniformToFill };
                badge.Child = img;
                FriendImageLoader.Load(
                    img,
                    $"https://media.retroachievements.org/Badge/{u.BadgeName}.png",
                    "recent-badge",
                    $"ach={u.AchievementId} game=[{u.GameTitle}]");
            }
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var stack = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = u.HardcoreMode != 0 ? $"[HC] {u.Title}" : u.Title,
            };
            stack.Children.Add(title);
            stack.Children.Add(new TextBlock
            {
                Text = $"{u.GameTitle} · {u.ConsoleName} · {u.Points} pts",
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            var when = new TextBlock
            {
                Text = FormatRelative(u.Date),
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(when, 2);
            grid.Children.Add(when);

            border.Child = grid;
            return border;
        }

        private static string FormatRelative(string isoDate)
        {
            if (string.IsNullOrWhiteSpace(isoDate)) return "";
            if (!DateTime.TryParseExact(isoDate, "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt)) return "";
            var delta = DateTime.UtcNow - dt;
            if (delta.TotalMinutes < 1) return "just now";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
            if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
            if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
            return dt.ToLocalTime().ToString("MMM d");
        }

        // ── Compare pane (Phase 5) ─────────────────────────────────────

        private async Task LoadCompareAsync()
        {
            if (_entry == null) return;
            CompareStatus.Text = "Loading comparison…";
            CompareStatus.Visibility = Visibility.Visible;
            CompareThemLabel.Text = _entry.Username.ToUpperInvariant();

            // Identify "my" username from the RA config.
            string? myUser = null;
            try { myUser = App.Configuration?.GetRetroAchievementsConfiguration()?.Username; } catch { }
            if (string.IsNullOrWhiteSpace(myUser))
            {
                CompareStatus.Text = "Set your RetroAchievements username in Preferences to compare libraries.";
                return;
            }

            try
            {
                // Friend's progress is cached 24h via ra_cache. Mine is
                // fetched fresh each open — small enough and ensures
                // delta arithmetic stays current.
                string friendCacheKey = $"friend_compare:{_entry.UserId}";
                _theirsProgress = ReadCachedProgress(friendCacheKey);
                if (_theirsProgress == null)
                {
                    _theirsProgress = await _api.GetUserCompletionProgressAsync(_entry.Username)
                                                .ConfigureAwait(true);
                    WriteCachedProgress(friendCacheKey, _theirsProgress);
                }

                _mineProgress = await _api.GetUserCompletionProgressAsync(myUser)
                                          .ConfigureAwait(true);

                RenderCompareSummary();
                RenderCompareRows();
                CompareStatus.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                CompareStatus.Text = $"Couldn't load comparison: {ex.Message}";
            }
        }

        private void CompareFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_comparePainted) return;
            RenderCompareRows();
        }

        private void RenderCompareSummary()
        {
            if (_mineProgress == null || _theirsProgress == null) return;

            int minePts = _mineProgress.Sum(p => p.NumAwarded);  // softcore-eq total
            int themPts = _theirsProgress.Sum(p => p.NumAwarded);
            int mineGames = _mineProgress.Count;
            int themGames = _theirsProgress.Count;
            int mineMastered = _mineProgress.Count(p => string.Equals(p.HighestAwardKind, "mastered", StringComparison.OrdinalIgnoreCase));
            int themMastered = _theirsProgress.Count(p => string.Equals(p.HighestAwardKind, "mastered", StringComparison.OrdinalIgnoreCase));

            CompareYouPoints.Text   = minePts.ToString("N0") + " ach.";
            CompareYouCounts.Text   = $"{mineGames} games · {mineMastered} mastered";
            CompareThemPoints.Text  = themPts.ToString("N0") + " ach.";
            CompareThemCounts.Text  = $"{themGames} games · {themMastered} mastered";

            int delta = themPts - minePts;
            CompareDeltaPoints.Text = delta == 0
                ? "tied"
                : (delta > 0 ? $"Δ +{delta:N0}" : $"Δ {delta:N0}");
            // The summed-NumAwarded above is an *achievements* count
            // (sum of softcore unlocks across all games), not points.
            // Use the achievement-specific FriendsCopy helper rather
            // than the points version with Replace munging.
            CompareDeltaCounts.Text = delta switch
            {
                > 0 => FriendsCopy.TheyreAheadByAch(delta),
                < 0 => FriendsCopy.YoureAheadByAch(Math.Abs(delta)),
                _   => "",
            };
        }

        private sealed class CompareRow
        {
            public int GameId; public string Title = ""; public string Console = ""; public string ImageIcon = "";
            public int Total; public int MineHC; public int Mine; public int ThemHC; public int Them;
            public int Gap; // theirs - mine (using softcore totals)
        }

        private void RenderCompareRows()
        {
            CompareGamesList.Items.Clear();
            if (_mineProgress == null || _theirsProgress == null) return;

            // Intersect on GameId; build merged rows.
            var mineByGame = _mineProgress.ToDictionary(p => p.GameId);
            var rows = new List<CompareRow>();
            foreach (var t in _theirsProgress)
            {
                if (!mineByGame.TryGetValue(t.GameId, out var m)) continue;
                rows.Add(new CompareRow
                {
                    GameId    = t.GameId,
                    Title     = t.Title,
                    Console   = t.ConsoleName,
                    ImageIcon = t.ImageIcon ?? "",
                    Total     = Math.Max(t.MaxPossible, m.MaxPossible),
                    Mine      = m.NumAwarded,
                    MineHC    = m.NumAwardedHardcore,
                    Them      = t.NumAwarded,
                    ThemHC    = t.NumAwardedHardcore,
                    Gap       = t.NumAwarded - m.NumAwarded,
                });
            }

            // Filter
            if (CompareHideIdentical.IsChecked == true)
                rows = rows.Where(r => r.Gap != 0).ToList();

            // Sort
            int sortIdx = CompareSortBox.SelectedIndex;
            rows = sortIdx switch
            {
                0 => rows.OrderByDescending(r => r.Gap).ThenBy(r => r.Title).ToList(),                  // catch up (they're ahead)
                1 => rows.OrderBy(r => r.Gap).ThenBy(r => r.Title).ToList(),                           // you're ahead
                2 => rows.OrderByDescending(r => r.Mine).ThenBy(r => r.Title).ToList(),                // my progress
                3 => rows.OrderByDescending(r => r.Them).ThenBy(r => r.Title).ToList(),                // their progress
                _ => rows.OrderBy(r => r.Title).ToList(),                                              // A → Z
            };

            if (rows.Count == 0)
            {
                CompareStatus.Text = (_mineProgress.Count == 0 || _theirsProgress.Count == 0)
                    ? "No games in common yet."
                    : "No games match the current filter.";
                CompareStatus.Visibility = Visibility.Visible;
                return;
            }
            CompareStatus.Visibility = Visibility.Collapsed;

            // Cap rendered rows at 250 — beyond that the UI gets sluggish
            // and the user wants to filter/sort anyway. If clipped, append
            // a footer row so the user knows there's more behind the cap.
            const int RenderCap = 250;
            int max = Math.Min(rows.Count, RenderCap);
            for (int i = 0; i < max; i++)
                CompareGamesList.Items.Add(BuildCompareRow(rows[i]));
            if (rows.Count > RenderCap)
            {
                CompareGamesList.Items.Add(new TextBlock
                {
                    Text = $"Showing {RenderCap} of {rows.Count} common games — narrow with filters or sort to see others.",
                    FontFamily = (FontFamily)FindResource("PrimaryFont"),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(16, 14, 16, 14),
                });
            }
        }

        private FrameworkElement BuildCompareRow(CompareRow r)
        {
            var border = new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Game icon
            var iconBorder = new Border
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("BgTertiaryBrush"),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!string.IsNullOrEmpty(r.ImageIcon))
            {
                var img = new Image { Stretch = Stretch.UniformToFill };
                iconBorder.Child = img;
                FriendImageLoader.Load(
                    img,
                    "https://media.retroachievements.org" + r.ImageIcon,
                    "compare-game",
                    $"game=[{r.Title}]");
            }
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            // Title + console + dual progress bars
            var stack = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = r.Title,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{r.Console} · {r.Total} achievements",
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 2, 0, 4),
            });
            stack.Children.Add(BuildProgressLine("You", r.Mine, r.Total));
            stack.Children.Add(BuildProgressLine(_entry?.Username ?? "Them", r.Them, r.Total));
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            // Gap pip (right side)
            var pipBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            string gapLabel;
            Brush gapBrush;
            if (r.Gap > 0)
            {
                gapLabel = $"−{r.Gap}";
                gapBrush = (Brush)FindResource("AccentBrush");
            }
            else if (r.Gap < 0)
            {
                gapLabel = $"+{Math.Abs(r.Gap)}";
                gapBrush = (Brush)FindResource("TextSecondaryBrush");
            }
            else
            {
                gapLabel = "even";
                gapBrush = (Brush)FindResource("TextMutedBrush");
            }
            pipBox.Children.Add(new TextBlock
            {
                Text = gapLabel,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = gapBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            pipBox.Children.Add(new TextBlock
            {
                Text = "GAP",
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(pipBox, 2);
            grid.Children.Add(pipBox);

            border.Child = grid;
            return border;
        }

        private FrameworkElement BuildProgressLine(string label, int got, int total)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelTb = new TextBlock
            {
                Text = label.Length > 6 ? label.Substring(0, 6) : label,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(labelTb, 0);
            grid.Children.Add(labelTb);

            // Progress bar
            var pb = new ProgressBar
            {
                Minimum = 0,
                Maximum = Math.Max(1, total),
                Value = Math.Min(got, total),
                Height = 6,
                Foreground = (Brush)FindResource("AccentBrush"),
                Background = (Brush)FindResource("BgTertiaryBrush"),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pb, 1);
            grid.Children.Add(pb);

            var countsTb = new TextBlock
            {
                Text = $"{got}/{total}",
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 50,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(countsTb, 2);
            grid.Children.Add(countsTb);

            return grid;
        }

        // ── Compare cache (24h TTL via ra_cache) ───────────────────────

        private List<RAUserCompletionProgressItem>? ReadCachedProgress(string key)
        {
            try
            {
                var row = _db.GetRaCache(key);
                if (row == null || string.IsNullOrEmpty(row.Payload)) return null;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if ((now - row.FetchedAt) >= row.TtlSeconds) return null;
                return System.Text.Json.JsonSerializer.Deserialize<List<RAUserCompletionProgressItem>>(row.Payload);
            }
            catch { return null; }
        }

        private void WriteCachedProgress(string key, List<RAUserCompletionProgressItem> list)
        {
            try
            {
                string payload = System.Text.Json.JsonSerializer.Serialize(list);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long ttl = (long)TimeSpan.FromHours(24).TotalSeconds;
                _db.SetRaCache(key, $"friend_compare:{_entry?.UserId ?? 0}", payload, now, ttl);
            }
            catch { /* cache write best-effort */ }
        }

        // ── Leaderboards pane (Phase 6) ────────────────────────────────

        private async Task<bool> PopulateLbGamePickerAsync()
        {
            if (_entry == null) return false;
            LbStatus.Text = "Loading common games…";
            LbStatus.Visibility = Visibility.Visible;
            LbGamePicker.Items.Clear();
            _lbGameIds.Clear();

            // Reuse the comparison's friend-progress cache. If Compare
            // tab hasn't been opened, fetch it now so the picker has
            // something to pick from.
            string friendCacheKey = $"friend_compare:{_entry.UserId}";
            _theirsProgress ??= ReadCachedProgress(friendCacheKey);
            string? myUser = null;
            try { myUser = App.Configuration?.GetRetroAchievementsConfiguration()?.Username; } catch { }
            if (string.IsNullOrWhiteSpace(myUser))
            {
                LbStatus.Text = "Set your RetroAchievements username in Preferences first.";
                return false;
            }
            try
            {
                if (_theirsProgress == null)
                {
                    _theirsProgress = await _api.GetUserCompletionProgressAsync(_entry.Username).ConfigureAwait(true);
                    WriteCachedProgress(friendCacheKey, _theirsProgress);
                }
                _mineProgress ??= await _api.GetUserCompletionProgressAsync(myUser).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LbStatus.Text = $"Couldn't load: {ex.Message}";
                return false;
            }

            // Intersect on GameId so the picker only offers games you
            // BOTH have history with. Sort by friend's recent activity
            // first (descending Date), then alphabetical.
            var mineByGame = (_mineProgress ?? new List<RAUserCompletionProgressItem>()).ToDictionary(p => p.GameId);
            var common = (_theirsProgress ?? new List<RAUserCompletionProgressItem>())
                .Where(p => mineByGame.ContainsKey(p.GameId))
                .OrderByDescending(p => p.MostRecentAwardedDate ?? "")
                .ThenBy(p => p.Title)
                .ToList();

            if (common.Count == 0)
            {
                LbStatus.Text = "No common games yet. Play something you both have to compare leaderboards.";
                return false;
            }

            foreach (var g in common)
            {
                LbGamePicker.Items.Add($"{g.Title} — {g.ConsoleName}");
                _lbGameIds.Add(g.GameId);
            }
            // Setting SelectedIndex synchronously raises SelectionChanged,
            // which immediately overwrites LbStatus.Text with "Loading
            // leaderboards…". No need to set a transient "Pick one"
            // message that the user would never see.
            LbGamePicker.SelectedIndex = 0;
            return true;
        }

        private async void LbGamePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            int idx = LbGamePicker.SelectedIndex;
            if (idx < 0 || idx >= _lbGameIds.Count) return;
            int raGameId = _lbGameIds[idx];
            await LoadLeaderboardsForGameAsync(raGameId).ConfigureAwait(true);
        }

        private async Task LoadLeaderboardsForGameAsync(int raGameId)
        {
            if (_entry == null) return;
            LbStatus.Text = "Loading leaderboards…";
            LbStatus.Visibility = Visibility.Visible;
            LbBoardsList.Items.Clear();

            string? myUser = null;
            try { myUser = App.Configuration?.GetRetroAchievementsConfiguration()?.Username; } catch { }
            if (string.IsNullOrWhiteSpace(myUser)) { LbStatus.Text = "Set your RetroAchievements username in Preferences first."; return; }

            try
            {
                // Friend-rank-across-all-LBs primitive per the audit:
                // one call per user gets their ranks on every LB for
                // this game, then we lay them out per-LB locally
                // instead of paging every entry list.
                var friendBoards = await _api.GetUserGameLeaderboardsAsync(_entry.Username, raGameId).ConfigureAwait(true);
                var myBoards     = await _api.GetUserGameLeaderboardsAsync(myUser, raGameId).ConfigureAwait(true);

                // Union of LB IDs the two of us have entries on.
                var byId = new Dictionary<int, (RAUserGameLeaderboard? mine, RAUserGameLeaderboard? theirs)>();
                foreach (var m in myBoards) byId[m.Id] = (m, null);
                foreach (var t in friendBoards)
                {
                    byId.TryGetValue(t.Id, out var prev);
                    byId[t.Id] = (prev.mine, t);
                }

                if (byId.Count == 0)
                {
                    // Empty union could mean either: (a) genuinely no
                    // leaderboard entries between the two of you, (b)
                    // the friend's profile is private (RA returns
                    // empty on 403). Hint at both so users don't
                    // assume their reader broke.
                    LbStatus.Text = "No leaderboard entries to compare yet. " +
                        "Either nobody's scored on this game's leaderboards, or your friend's profile is private.";
                    return;
                }
                LbStatus.Visibility = Visibility.Collapsed;

                foreach (var kv in byId.OrderBy(k => k.Value.mine?.Title ?? k.Value.theirs?.Title))
                {
                    LbBoardsList.Items.Add(BuildLbRow(kv.Value.mine, kv.Value.theirs, myUser, _entry.Username));
                }
            }
            catch (Exception ex)
            {
                LbStatus.Text = $"Couldn't load leaderboards: {ex.Message}";
            }
        }

        private FrameworkElement BuildLbRow(
            RAUserGameLeaderboard? mine, RAUserGameLeaderboard? theirs,
            string myName, string friendName)
        {
            var border = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            var stack = new StackPanel();

            string title = mine?.Title ?? theirs?.Title ?? "";
            string format = mine?.Format ?? theirs?.Format ?? "";
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            string desc = mine?.Description ?? theirs?.Description ?? "";
            if (!string.IsNullOrEmpty(desc))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontFamily = (FontFamily)FindResource("PrimaryFont"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    // Wrap on space; many LB descriptions are 1-3 lines.
                    // CharacterEllipsis on a single line truncated useful
                    // context (per audit).
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 6),
                });
            }

            // Two-row score block (mine + theirs).
            stack.Children.Add(BuildLbScoreLine(myName, mine?.UserEntry));
            stack.Children.Add(BuildLbScoreLine(friendName, theirs?.UserEntry));

            border.Child = stack;
            return border;
        }

        private FrameworkElement BuildLbScoreLine(string who, RALeaderboardEntry? entry)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = who,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var rank = new TextBlock
            {
                Text = entry == null ? "—" : $"#{entry.Rank}",
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 1);
            grid.Children.Add(rank);

            var score = new TextBlock
            {
                Text = entry == null ? "no score yet" : entry.FormattedScore,
                FontFamily = (FontFamily)FindResource("PrimaryFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(score, 2);
            grid.Children.Add(score);

            return grid;
        }
    }
}
