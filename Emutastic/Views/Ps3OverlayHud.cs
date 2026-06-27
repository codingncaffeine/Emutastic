using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Emutastic.Views
{
    /// <summary>
    /// The in-game overlay for a PlayStation 3 title. Because the game renders in the external
    /// emulator's own window (a foreign window we can't draw onto), the HUD lives in a separate
    /// transparent, click-through, always-on-top window positioned over the game — the same approach
    /// the hardware-rendered cores use. It shows a control pill on mouse movement and auto-hides.
    /// Only the actions that work for an external emulator are offered: close, fullscreen, and a
    /// settings cog with the internal-resolution choice.
    /// </summary>
    public sealed class Ps3OverlayHud : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        // Segoe MDL2 Assets icon glyphs.
        private static readonly string GlyphPower = ((char)0xE7E8).ToString();
        private static readonly string GlyphFullScreen = ((char)0xE740).ToString();
        private static readonly string GlyphSettings = ((char)0xE713).ToString();

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;

        private readonly Window _hud;
        private readonly Border _pill;
        private readonly Border _cogMenu;
        private readonly DispatcherTimer _poller;
        private readonly DispatcherTimer _hideTimer;
        private readonly Func<IntPtr> _renderWindow;
        private POINT _lastCursor;
        private bool _visible;
        private bool _disposed;

        public Ps3OverlayHud(Window owner, Func<IntPtr> renderWindow, Action onClose, Action onFullscreen)
        {
            _renderWindow = renderWindow;

            _cogMenu = BuildCogMenu();
            _pill = BuildPill(onClose, onFullscreen);

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 28),
            };
            stack.Children.Add(_cogMenu);
            stack.Children.Add(_pill);

            var layer = new Grid { Background = null }; // null background = clicks pass through except on the pill
            layer.Children.Add(stack);

            _hud = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Owner = owner,
                ShowActivated = false,
                Opacity = 0,
                Content = layer,
            };
            _hud.SourceInitialized += (_, _) =>
            {
                IntPtr h = new WindowInteropHelper(_hud).Handle;
                if (h == IntPtr.Zero) return;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
            };

            _poller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _poller.Tick += OnPoll;

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _hideTimer.Tick += (_, _) => { if (_cogMenu.Visibility != Visibility.Visible) Hide(); };
        }

        public void Start()
        {
            GetCursorPos(out _lastCursor);
            _hud.Show();
            Reposition();
            _poller.Start();
        }

        /// <summary>Keeps the HUD window aligned over the game's render window.</summary>
        public void Reposition()
        {
            if (_disposed) return;
            IntPtr render = _renderWindow();
            IntPtr hud = new WindowInteropHelper(_hud).Handle;
            if (render == IntPtr.Zero || hud == IntPtr.Zero) return;
            if (!GetWindowRect(render, out RECT r)) return;
            SetWindowPos(hud, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE);
        }

        private void OnPoll(object? sender, EventArgs e)
        {
            if (_disposed) return;
            Reposition();
            if (!GetCursorPos(out POINT p)) return;
            if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
            {
                _lastCursor = p;
                if (CursorOverGame(p)) Show();
            }
        }

        private bool CursorOverGame(POINT p)
        {
            IntPtr render = _renderWindow();
            if (render == IntPtr.Zero || !GetWindowRect(render, out RECT r)) return false;
            return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        }

        private void Show()
        {
            _hideTimer.Stop();
            _hideTimer.Start();
            if (_visible) return;
            _visible = true;
            SetHudInteractive(true);
            _hud.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }

        private void Hide()
        {
            _hideTimer.Stop();
            if (!_visible) return;
            _visible = false;
            _cogMenu.Visibility = Visibility.Collapsed;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) => { if (!_visible) SetHudInteractive(false); };
            _hud.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // When hidden, make the whole HUD window click-through so the game gets the mouse.
        private void SetHudInteractive(bool interactive)
        {
            IntPtr h = new WindowInteropHelper(_hud).Handle;
            if (h == IntPtr.Zero) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            ex = interactive ? (ex & ~WS_EX_TRANSPARENT) : (ex | WS_EX_TRANSPARENT);
            SetWindowLong(h, GWL_EXSTYLE, ex);
        }

        // ── UI construction ──
        private Border BuildPill(Action onClose, Action onFullscreen)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(PillButton(GlyphPower, "Close", (_, _) => onClose()));
            row.Children.Add(PillButton(GlyphFullScreen, "Fullscreen", (_, _) => onFullscreen()));
            row.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 1, Height = 26, Margin = new Thickness(6, 0, 6, 0),
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(PillButton(GlyphSettings, "Settings", (_, _) =>
                _cogMenu.Visibility = _cogMenu.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0x1C, 0x1C, 0x1E)),
                CornerRadius = new CornerRadius(28),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = row,
            };
        }

        private Button PillButton(string glyph, string tip, RoutedEventHandler onClick)
        {
            var btn = new Button
            {
                Width = 52, Height = 52,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18 },
            };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(26));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))));
            template.Triggers.Add(hover);
            btn.Template = template;
            btn.Click += onClick;
            return btn;
        }

        private Border BuildCogMenu()
        {
            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Internal resolution",
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6),
            });

            var combo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            var options = new (string label, int scale)[] { ("Native (720p)", 100), ("1080p", 150), ("1440p", 200), ("4K", 300) };
            int current = App.Configuration?.GetEmulatorConfiguration().Ps3ResolutionScale ?? 100;
            foreach (var o in options) combo.Items.Add(new ComboBoxItem { Content = o.label, Tag = o.scale });
            int idx = Array.FindIndex(options, o => o.scale == current);
            combo.SelectedIndex = idx >= 0 ? idx : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem ci && ci.Tag is int v && App.Configuration != null)
                {
                    var cfg = App.Configuration.GetEmulatorConfiguration();
                    cfg.Ps3ResolutionScale = v;
                    App.Configuration.SetEmulatorConfiguration(cfg);
                    _ = App.Configuration.SaveAsync();
                }
            };
            panel.Children.Add(combo);
            panel.Children.Add(new TextBlock
            {
                Text = "Applies on the next launch.",
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                FontSize = 10,
                Margin = new Thickness(0, 6, 0, 0),
            });

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0F, 0x0F, 0x11)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed,
                Child = panel,
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _poller.Stop();
            _hideTimer.Stop();
            try { _hud.Close(); } catch { /* owner may already be closing */ }
        }
    }
}
