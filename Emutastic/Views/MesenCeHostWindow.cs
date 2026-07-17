using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Emutastic.Models;
using Emutastic.Services.MesenCe;

namespace Emutastic.Views
{
    /// <summary>
    /// In-process host window for a game running on the external Mesen 2 (MesenCE)
    /// emulator — used for Mesen 2-format HD mods (pack v107+) that the classic
    /// libretro core can't render. Launches the emulator, re-parents its window
    /// under a slim themed title bar, keeps it fitted on resize, and shuts it down
    /// cleanly on close. Same shell pattern as the PS3 host.
    /// </summary>
    public sealed class MesenCeHostWindow : Window
    {
        private const double TitleBarHeight = 32;

        private readonly Game _game;
        private readonly string _romPath;
        private readonly MesenCeSession _session = new();
        private readonly TextBlock _status;
        private readonly Border _titleBar;
        private DispatcherTimer? _acquire;
        private DateTime _startUtc;
        private bool _embedded;
        private bool _sessionReported;
        private bool _fs;

        private readonly bool _windowsChrome = App.Configuration?.GetThemeConfiguration()?.UseWindowsChrome == true;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Raised on the UI thread with elapsed play-seconds once the session ends.</summary>
        public event Action<int>? SessionEnded;

        public MesenCeHostWindow(Game game, string romPath)
        {
            _game = game;
            _romPath = romPath;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Width = 1024;
            Height = 900 + TitleBarHeight;
            Background = Res("BgPrimaryBrush", Color.FromRgb(0x12, 0x12, 0x14));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_game.Title) ? "Mesen 2" : _game.Title,
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

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_titleBar, 0);
            Grid.SetRow(hostArea, 1);
            root.Children.Add(_titleBar);
            root.Children.Add(hostArea);
            Content = root;

            if (_windowsChrome)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                Title = string.IsNullOrWhiteSpace(_game.Title) ? "Mesen 2" : _game.Title;
                _titleBar.Visibility = Visibility.Collapsed;
                root.RowDefinitions[0].Height = new GridLength(0);
            }

            SourceInitialized += OnSourceInitialized;
            SizeChanged += (_, _) => { if (_embedded) _session.FitTo(Handle, TopOffsetPx()); };
            StateChanged += (_, _) => { if (_embedded) _session.FitTo(Handle, TopOffsetPx()); };
            Closing += OnClosing;
        }

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        private int TopOffsetPx()
        {
            if (_windowsChrome || _fs) return 0;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            return (int)Math.Round(TitleBarHeight * scale);
        }

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
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = _windowsChrome ? WindowStyle.SingleBorderWindow : WindowStyle.None;
                _titleBar.Visibility = _windowsChrome ? Visibility.Collapsed : Visibility.Visible;
                WindowState = WindowState.Normal;
            }
            if (_embedded) _session.FitTo(Handle, TopOffsetPx());
        }

        private static Brush Res(string key, Color fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_windowsChrome)
            {
                try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { }
            }

            if (!MesenCeRuntime.IsInstalled())
            {
                _status.Text = "Mesen 2 isn't installed yet.\n\nInstall it from Preferences → Cores (Nintendo → NES — Mesen 2).";
                return;
            }
            if (string.IsNullOrEmpty(_romPath) || !System.IO.File.Exists(_romPath))
            {
                _status.Text = "Game file not found.";
                return;
            }

            MesenCeRuntime.PrepareForEmbedding();
            MesenCeRuntime.SyncActiveMod(System.IO.Path.GetFileNameWithoutExtension(_romPath));
            _startUtc = DateTime.UtcNow;

            if (!_session.Start(MesenCeRuntime.GetExe(), _romPath))
            {
                _status.Text = "Couldn't start the emulator.";
                return;
            }

            _acquire = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
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

            if (_embedded)
            {
                if (!_session.WindowAlive)
                {
                    // Emulator window gone but process alive — treat as ended.
                    _acquire?.Stop();
                    Close();
                }
                return;
            }

            if (_session.TryAcquireWindow() && _session.EmbedInto(Handle, TopOffsetPx()))
            {
                _embedded = true;
                _status.Visibility = Visibility.Collapsed;
            }
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _acquire?.Stop();
            _session.CloseGracefully();
            _session.Dispose();

            if (!_sessionReported)
            {
                _sessionReported = true;
                int secs = (int)Math.Max(0, (DateTime.UtcNow - _startUtc).TotalSeconds);
                if (_startUtc != default && secs > 0) SessionEnded?.Invoke(secs);
            }
        }
    }
}
