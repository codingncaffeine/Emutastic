using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Emutastic.Services.Ps3
{
    /// <summary>
    /// Drives the external PlayStation 3 emulator process for a single title: launches it
    /// windowed with no game-list UI, locates its render window once it appears, re-parents
    /// that window into a host container, keeps it fitted, and shuts it down cleanly.
    ///
    /// The emulator runs in its own process, so a fault there is contained to that process
    /// and cannot bring down the application. Window discovery uses the live render window
    /// rather than the process's reported main window, which is created off the main thread.
    /// </summary>
    public sealed class Rpcs3Session : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);

        private delegate bool EnumProc(IntPtr h, IntPtr p);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        // Frame bits stripped so the embedded window has no caption/border of its own.
        private const int StyleStrip = unchecked((int)0x80000000) | 0x00C00000 | 0x00040000 | 0x00800000;
        private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOZORDER = 0x0004, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
        private const uint WM_CLOSE = 0x0010;

        private Process? _proc;
        private IntPtr _renderWindow;
        private IntPtr _findResult;
        private uint _findPid;

        public bool HasExited => _proc?.HasExited ?? true;
        public IntPtr RenderWindow => _renderWindow;

        /// <summary>True while the currently embedded render window still exists.</summary>
        public bool RenderWindowAlive => _renderWindow != IntPtr.Zero && IsWindow(_renderWindow);

        /// <summary>
        /// Drops the cached render window so the next acquire finds a freshly created one — e.g.
        /// after a launcher boot chains to the game and spawns a new window (issue #14255).
        /// </summary>
        public void ForgetRenderWindow() => _renderWindow = IntPtr.Zero;

        /// <summary>Launches the emulator on the given boot file, windowed and without the game-list UI.</summary>
        public bool Start(string emulatorExe, string bootPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = emulatorExe,
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(emulatorExe) ?? "",
            };
            psi.ArgumentList.Add(bootPath);
            psi.ArgumentList.Add("--no-gui");
            try { _proc = Process.Start(psi); }
            catch (Exception ex) { Trace.WriteLine($"[Ps3] start failed: {ex.Message}"); return false; }
            return _proc != null;
        }

        private bool Collect(IntPtr h, IntPtr l)
        {
            if (GetWindowThreadProcessId(h, out uint pid) != 0 && pid == _findPid && IsWindowVisible(h))
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                // The render window's title carries a live readout; other top-level windows
                // owned by the process (splash, helpers) do not.
                if (sb.ToString().StartsWith("FPS:", StringComparison.Ordinal)) { _findResult = h; return false; }
            }
            return true;
        }

        /// <summary>
        /// Tries to find the emulator's render window. Returns false until it has been created.
        /// Re-queries the current window each call, so a window swapped in after a chained boot
        /// is picked up automatically.
        /// </summary>
        public bool TryAcquireRenderWindow()
        {
            if (_renderWindow != IntPtr.Zero) return true;
            if (_proc == null || _proc.HasExited) return false;
            _findResult = IntPtr.Zero;
            _findPid = (uint)_proc.Id;
            EnumWindows(Collect, IntPtr.Zero);
            if (_findResult != IntPtr.Zero) { _renderWindow = _findResult; return true; }
            return false;
        }

        /// <summary>
        /// Re-parents the render window into the host, strips its frame, and fits it to the host's
        /// client area. Returns true when the OS confirms the new parent.
        /// </summary>
        public bool EmbedInto(IntPtr host, int topOffset = 0, int bottomOffset = 0)
        {
            if (_renderWindow == IntPtr.Zero || host == IntPtr.Zero) return false;
            SetParent(_renderWindow, host);
            int style = GetWindowLong(_renderWindow, GWL_STYLE);
            SetWindowLong(_renderWindow, GWL_STYLE, (style & ~StyleStrip) | WS_CHILD | WS_VISIBLE);
            SetWindowPos(_renderWindow, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOMOVE | SWP_NOSIZE);
            FitTo(host, topOffset, bottomOffset);
            return GetParent(_renderWindow) == host;
        }

        /// <summary>
        /// Resizes the embedded render window to fill the host's client area between an optional top
        /// and bottom offset (physical pixels) reserved for the host's own chrome (title/status bar).
        /// </summary>
        public void FitTo(IntPtr host, int topOffset = 0, int bottomOffset = 0)
        {
            if (_renderWindow == IntPtr.Zero || host == IntPtr.Zero) return;
            if (GetClientRect(host, out RECT r))
                MoveWindow(_renderWindow, 0, topOffset, r.Right - r.Left, (r.Bottom - r.Top) - topOffset - bottomOffset, true);
        }

        /// <summary>The render window's current title, which carries a live "FPS: n" readout.</summary>
        public string RenderTitle
        {
            get
            {
                if (_renderWindow == IntPtr.Zero) return "";
                var sb = new StringBuilder(256);
                GetWindowText(_renderWindow, sb, sb.Capacity);
                return sb.ToString();
            }
        }

        /// <summary>
        /// Asks the emulator to close cleanly via its window (a hard kill produces a noisy
        /// crash-style exit and a system error chime). Falls back to terminating after the timeout.
        /// </summary>
        public void CloseGracefully(int timeoutMs = 8000)
        {
            try
            {
                if (_proc == null || _proc.HasExited) return;
                if (_renderWindow != IntPtr.Zero) SendMessage(_renderWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (!_proc.WaitForExit(timeoutMs)) _proc.Kill();
            }
            catch (Exception ex) { Trace.WriteLine($"[Ps3] close failed: {ex.Message}"); }
        }

        public void Dispose() { try { _proc?.Dispose(); } catch { } }
    }
}
