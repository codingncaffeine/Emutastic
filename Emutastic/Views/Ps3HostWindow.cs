using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Emutastic.Models;
using Emutastic.Services.Ps3;

namespace Emutastic.Views
{
    /// <summary>
    /// In-process host window for a PlayStation 3 title. Launches the external emulator and
    /// re-parents its render window into the area below a slim themed title bar. The emulator's
    /// own process provides crash isolation; this window owns the frame, keeps the embedded
    /// output fitted on resize, and shuts the emulator down cleanly on close.
    /// </summary>
    public sealed class Ps3HostWindow : Window
    {
        private const double TitleBarHeight = 32;
        private const double StatusBarHeight = 22;

        private readonly Game _game;
        private readonly bool _fullscreen;
        private readonly Rpcs3Session _session = new();
        private readonly TextBlock _status;
        private readonly Border _titleBar;
        private readonly Border _statusBar;
        private readonly TextBlock _fpsText;
        private DispatcherTimer? _acquire;
        private DateTime _startUtc;
        private bool _embedded;
        private bool _ended;
        private bool _fs;
        private Ps3OverlayHud? _overlay;

        // Follow the user's window-chrome choice (Preferences → theme): true = native Windows
        // title bar + buttons; false = the custom frameless (macOS-style) title bar.
        private readonly bool _windowsChrome = App.Configuration?.GetThemeConfiguration()?.UseWindowsChrome == true;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Raised on the UI thread with elapsed play-seconds once the session ends.</summary>
        public event Action<int>? SessionEnded;

        public Ps3HostWindow(Game game, bool fullscreen = false)
        {
            _game = game;
            _fullscreen = fullscreen;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Width = 1280;
            Height = 720 + TitleBarHeight;
            Background = Res("BgPrimaryBrush", Color.FromRgb(0x12, 0x12, 0x14));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // ── Title bar (matches the standard emulator window: title + traffic-light buttons) ──
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_game.Title) ? "PlayStation 3" : _game.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextSecondaryBrush", Colors.Gainsboro),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };

            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            dots.Children.Add(MakeDot(Res("TrafficYellowBrush", Color.FromRgb(0xFF, 0xBD, 0x2E)), (_, _) => WindowState = WindowState.Minimized));
            dots.Children.Add(MakeDot(Res("GreenBrush", Color.FromRgb(0x28, 0xC8, 0x40)), (_, _) => ToggleFullscreen()));
            dots.Children.Add(MakeDot(Res("TrafficRedBrush", Color.FromRgb(0xFF, 0x5F, 0x57)), (_, _) => Close()));

            var barContent = new DockPanel();
            DockPanel.SetDock(dots, Dock.Right);
            barContent.Children.Add(dots);
            barContent.Children.Add(title);

            _titleBar = new Border
            {
                Height = TitleBarHeight,
                Background = Res("BgSecondaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1C)),
                Child = barContent,
            };
            _titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                if (e.ClickCount == 2) ToggleFullscreen();
                else DragMove();
            };

            // ── Game host area (the emulator window embeds here) ──
            _status = new TextBlock
            {
                Text = "Loading…",
                Foreground = Res("TextSecondaryBrush", Colors.White),
                FontSize = 18,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var hostArea = new Grid { Background = Brushes.Black, Children = { _status } };

            // ── Bottom status bar (live FPS, like the other consoles) ──
            _fpsText = new TextBlock
            {
                Text = "",
                Foreground = Res("TextSecondaryBrush", Colors.Gainsboro),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            _statusBar = new Border
            {
                Height = StatusBarHeight,
                Background = Res("BgSecondaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1C)),
                Child = _fpsText,
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_titleBar, 0);
            Grid.SetRow(hostArea, 1);
            Grid.SetRow(_statusBar, 2);
            root.Children.Add(_titleBar);
            root.Children.Add(hostArea);
            root.Children.Add(_statusBar);
            Content = root;

            // Native Windows chrome: use the system title bar + buttons and drop the custom one.
            if (_windowsChrome && !_fullscreen)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                Title = string.IsNullOrWhiteSpace(_game.Title) ? "PlayStation 3" : _game.Title;
                _titleBar.Visibility = Visibility.Collapsed;
                root.RowDefinitions[0].Height = new GridLength(0);
            }

            SourceInitialized += OnSourceInitialized;
            SizeChanged += (_, _) => { if (_embedded) _session.FitTo(Handle, TopOffsetPx(), BottomOffsetPx()); _overlay?.Reposition(); };
            LocationChanged += (_, _) => _overlay?.Reposition();
            StateChanged += (_, _) => { if (_embedded) _session.FitTo(Handle, TopOffsetPx(), BottomOffsetPx()); _overlay?.Reposition(); };
            Closing += OnClosing;
        }

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        // Title-bar height in physical pixels (0 in fullscreen, where the bar is hidden).
        private int TopOffsetPx()
        {
            if (_windowsChrome || _fullscreen || _fs) return 0;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            return (int)Math.Round(TitleBarHeight * scale);
        }

        // Status-bar height in physical pixels (0 in fullscreen, where the bar is hidden).
        private int BottomOffsetPx()
        {
            if (_fullscreen || _fs) return 0;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            return (int)Math.Round(StatusBarHeight * scale);
        }

        private void UpdateFps()
        {
            var m = System.Text.RegularExpressions.Regex.Match(_session.RenderTitle, @"FPS:\s*([\d.]+)");
            _fpsText.Text = m.Success ? $"FPS  {m.Groups[1].Value}" : "";
        }

        // A circular title-bar button (the traffic-light style used by the emulator window).
        private Button MakeDot(Brush fill, RoutedEventHandler onClick)
        {
            var btn = new Button { Width = 12, Height = 12, Margin = new Thickness(5, 0, 0, 0), Cursor = Cursors.Hand, Focusable = false };
            var template = new ControlTemplate(typeof(Button));
            var ellipse = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            ellipse.SetValue(System.Windows.Shapes.Shape.FillProperty, fill);
            template.VisualTree = ellipse;
            btn.Template = template;
            btn.Click += onClick;
            return btn;
        }

        private void ToggleFullscreen()
        {
            _fs = !_fs;
            if (_fs)
            {
                WindowStyle = WindowStyle.None;
                _titleBar.Visibility = Visibility.Collapsed;
                _statusBar.Visibility = Visibility.Collapsed;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = _windowsChrome ? WindowStyle.SingleBorderWindow : WindowStyle.None;
                _titleBar.Visibility = _windowsChrome ? Visibility.Collapsed : Visibility.Visible;
                _statusBar.Visibility = Visibility.Visible;
                WindowState = WindowState.Normal;
            }
            if (_embedded) _session.FitTo(Handle, TopOffsetPx(), BottomOffsetPx());
        }

        private static Brush Res(string key, Color fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_fullscreen)
            {
                _titleBar.Visibility = Visibility.Collapsed;
                _statusBar.Visibility = Visibility.Collapsed;
                WindowState = WindowState.Maximized;
            }
            else if (_windowsChrome)
            {
                // Dark native title bar to match the app's theme (DWMWA_USE_IMMERSIVE_DARK_MODE).
                try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { }
            }

            if (!Rpcs3Runtime.IsInstalled())
            {
                _status.Text = "PlayStation 3 support isn't installed yet.\n\nInstall it from the Cores / Extras tab.";
                return;
            }
            if (string.IsNullOrEmpty(_game.RomPath) || !System.IO.File.Exists(_game.RomPath))
            {
                _status.Text = "Game file not found.";
                return;
            }

            Rpcs3Runtime.PrepareForEmbedding();
            _startUtc = DateTime.UtcNow;

            if (!_session.Start(Rpcs3Runtime.GetExe(), _game.RomPath))
            {
                _status.Text = "Couldn't start the emulator.";
                return;
            }

            _status.Text = Rpcs3Runtime.HasAnyCache()
                ? "Loading…"
                : "Starting…\n\nThe first launch compiles shaders and may take a minute. Later launches are quick.";

            _acquire = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _acquire.Tick += OnAcquireTick;
            _acquire.Start();
        }

        private void OnAcquireTick(object? sender, EventArgs e)
        {
            if (_session.HasExited)
            {
                _acquire?.Stop();
                Close();
                return;
            }

            // Once embedded, keep watching: if the embedded window dies but the emulator is still
            // running, a launcher boot has chained to the game and spawned a new window (#14255).
            if (_embedded)
            {
                if (!_session.RenderWindowAlive)
                {
                    _embedded = false;
                    _session.ForgetRenderWindow();
                    _status.Visibility = Visibility.Visible;
                    return;
                }
                UpdateFps();
                return;
            }

            if (!_session.TryAcquireRenderWindow()) return;
            _embedded = _session.EmbedInto(Handle, TopOffsetPx(), BottomOffsetPx());
            _status.Visibility = _embedded ? Visibility.Collapsed : Visibility.Visible;

            if (_embedded && _overlay == null)
            {
                _overlay = new Ps3OverlayHud(this, () => _session.RenderWindow, Close, ToggleFullscreen);
                _overlay.Start();
            }
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _acquire?.Stop();
            _overlay?.Dispose();
            _session.CloseGracefully();
            _session.Dispose();

            if (_ended) return;
            _ended = true;
            int secs = _startUtc == default ? 0 : Math.Max(0, (int)(DateTime.UtcNow - _startUtc).TotalSeconds - 3);
            try { SessionEnded?.Invoke(secs); } catch { /* caller (and main app) may already be tearing down */ }
        }
    }
}
