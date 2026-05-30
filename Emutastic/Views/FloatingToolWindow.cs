using System;
using System.Windows;
using System.Windows.Input;

namespace Emutastic.Views
{
    /// <summary>
    /// Base class for Emutastic's floating tool windows (per-game Notes, and the
    /// PDF manual viewer). Borderless custom chrome so we can host a stay-on-top
    /// pin and a classic-Mac "WindowShade" roll-up in the title bar.
    ///
    /// AllowsTransparency stays FALSE on purpose: these windows may host an HWND
    /// child (WebView2 in the manual viewer), and a transparent WPF window cannot
    /// host child HWNDs without airspace/hit-test breakage. Borderless resize is
    /// provided by ResizeMode.CanResizeWithGrip (a bottom-right grip) rather than
    /// WindowChrome, which avoids the WindowStyle=None maximize-covers-taskbar
    /// pitfall entirely — sizing is via the grip, no maximize button.
    /// </summary>
    public class FloatingToolWindow : Window
    {
        private double _restoreHeight;

        /// <summary>True while the window is rolled up to just its title bar.</summary>
        public bool IsRolledUp { get; private set; }

        /// <summary>Height the window collapses to when rolled up (title-bar height).</summary>
        public double TitleBarHeight { get; set; } = 40;

        /// <summary>Raised when the pin (Topmost) state changes so the title bar can refresh its glyph.</summary>
        public event EventHandler? PinChanged;

        protected FloatingToolWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            MinWidth = 380;
            MinHeight = 240;

            // Take over the non-client area so Windows doesn't paint its default light
            // caption/glass strip across the top of these borderless windows. CaptionHeight=0
            // + GlassFrameThickness=0 removes that white strip; ResizeBorderThickness keeps
            // edge-resize. (AllowsTransparency stays false for WebView2 compatibility.)
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        }

        public void TogglePin()
        {
            Topmost = !Topmost;
            PinChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleRollUp()
        {
            if (IsRolledUp)
            {
                IsRolledUp = false;
                MinHeight = 240;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                Height = _restoreHeight;
            }
            else
            {
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                _restoreHeight = ActualHeight;
                IsRolledUp = true;
                ResizeMode = ResizeMode.NoResize;
                MinHeight = TitleBarHeight;
                Height = TitleBarHeight;
            }
        }

        /// <summary>Drags the window from a title-bar mouse-down. Safe to call mid-gesture.</summary>
        public void BeginDrag()
        {
            try
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
                    DragMove();
            }
            catch (InvalidOperationException) { /* button released before DragMove latched */ }
        }
    }
}
