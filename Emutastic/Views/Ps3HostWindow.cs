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
        private const double TitleBarHeight = 34;

        private readonly Game _game;
        private readonly bool _fullscreen;
        private readonly Rpcs3Session _session = new();
        private readonly TextBlock _status;
        private readonly Border _titleBar;
        private DispatcherTimer? _acquire;
        private DateTime _startUtc;
        private bool _embedded;
        private bool _ended;

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

            // ── Title bar (drag to move, close button) ──
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_game.Title) ? "PlayStation 3" : _game.Title,
                Foreground = Res("TextPrimaryBrush", Colors.White),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            var closeBtn = new Button
            {
                Content = "✕",
                Width = 46,
                Foreground = Res("TextSecondaryBrush", Colors.Gainsboro),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 13,
            };
            closeBtn.Click += (_, _) => Close();

            var barContent = new DockPanel();
            DockPanel.SetDock(closeBtn, Dock.Right);
            barContent.Children.Add(closeBtn);
            barContent.Children.Add(title);

            _titleBar = new Border
            {
                Height = TitleBarHeight,
                Background = Res("BgSecondaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1C)),
                Child = barContent,
            };
            _titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

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

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_titleBar, 0);
            Grid.SetRow(hostArea, 1);
            root.Children.Add(_titleBar);
            root.Children.Add(hostArea);
            Content = root;

            SourceInitialized += OnSourceInitialized;
            SizeChanged += (_, _) => { if (_embedded) _session.FitTo(Handle, TopOffsetPx()); };
            Closing += OnClosing;
        }

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        // Title-bar height in physical pixels (0 in fullscreen, where the bar is hidden).
        private int TopOffsetPx()
        {
            if (_fullscreen) return 0;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            return (int)Math.Round(TitleBarHeight * scale);
        }

        private static Brush Res(string key, Color fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_fullscreen)
            {
                _titleBar.Visibility = Visibility.Collapsed;
                WindowState = WindowState.Maximized;
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
                }
                return;
            }

            if (!_session.TryAcquireRenderWindow()) return;
            _embedded = _session.EmbedInto(Handle, TopOffsetPx());
            _status.Visibility = _embedded ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _acquire?.Stop();
            _session.CloseGracefully();
            _session.Dispose();

            if (_ended) return;
            _ended = true;
            int secs = _startUtc == default ? 0 : Math.Max(0, (int)(DateTime.UtcNow - _startUtc).TotalSeconds - 3);
            try { SessionEnded?.Invoke(secs); } catch { /* caller (and main app) may already be tearing down */ }
        }
    }
}
