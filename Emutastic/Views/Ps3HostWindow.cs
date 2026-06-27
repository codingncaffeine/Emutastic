using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Emutastic.Models;
using Emutastic.Services.Ps3;

namespace Emutastic.Views
{
    /// <summary>
    /// In-process host window for a PlayStation 3 title. It launches the external emulator and
    /// re-parents the emulator's render window into itself, so the game appears inside the app
    /// shell. The emulator's own process provides crash isolation; this window owns the frame,
    /// keeps the embedded output fitted on resize, and shuts the emulator down cleanly on close.
    /// </summary>
    public sealed class Ps3HostWindow : Window
    {
        private readonly Game _game;
        private readonly bool _fullscreen;
        private readonly Rpcs3Session _session = new();
        private readonly TextBlock _status;
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

            Title = "Emutastic";
            Width = 1280;
            Height = 720;
            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _status = new TextBlock
            {
                Text = "Loading…",
                Foreground = Brushes.White,
                FontSize = 18,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Content = new Grid { Children = { _status } };

            SourceInitialized += OnSourceInitialized;
            SizeChanged += (_, _) => { if (_embedded) _session.FitTo(Handle); };
            Closing += OnClosing;
        }

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_fullscreen)
            {
                WindowStyle = WindowStyle.None;
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

            // First launch compiles modules/shaders to an on-disk cache; later launches reuse it.
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

            // Once embedded, keep watching: if the embedded window dies but the emulator is
            // still running, a launcher boot has chained to the game and spawned a new window
            // (issue #14255). Drop the stale handle and re-acquire + re-embed the new one.
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
            _embedded = _session.EmbedInto(Handle);
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
