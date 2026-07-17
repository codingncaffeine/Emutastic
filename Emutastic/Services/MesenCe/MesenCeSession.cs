using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Emutastic.Services.MesenCe
{
    /// <summary>
    /// Drives the external Mesen 2 (MesenCE) process for a single game: launches it
    /// on the ROM, locates its main window once created, re-parents it into a host
    /// container, keeps it fitted, and shuts it down cleanly. Same crash-isolation
    /// posture as the PS3 session — a fault in the emulator can't take the app down.
    /// </summary>
    public sealed class MesenCeSession : IDisposable
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
        private const int StyleStrip = unchecked((int)0x80000000) | 0x00C00000 | 0x00040000 | 0x00800000;
        private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOZORDER = 0x0004, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
        private const uint WM_CLOSE = 0x0010;

        private Process? _proc;
        private IntPtr _window;
        private IntPtr _findResult;
        private uint _findPid;

        public bool HasExited => _proc?.HasExited ?? true;
        public bool WindowAlive => _window != IntPtr.Zero && IsWindow(_window);

        /// <summary>Launches the emulator on the given ROM (windowed; embedding strips the frame).</summary>
        public bool Start(string emulatorExe, string romPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = emulatorExe,
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(emulatorExe) ?? "",
            };
            psi.ArgumentList.Add(romPath);
            try { _proc = Process.Start(psi); }
            catch (Exception ex) { Trace.WriteLine($"[MesenCE] start failed: {ex.Message}"); return false; }
            return _proc != null;
        }

        private bool Collect(IntPtr h, IntPtr l)
        {
            if (GetWindowThreadProcessId(h, out uint pid) != 0 && pid == _findPid && IsWindowVisible(h))
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                // The main window's title always contains the emulator's name
                // ("Mesen" or "<game> - Mesen"); helper/splash windows don't.
                if (sb.ToString().IndexOf("Mesen", StringComparison.OrdinalIgnoreCase) >= 0)
                { _findResult = h; return false; }
            }
            return true;
        }

        /// <summary>Tries to find the emulator's main window. False until it exists.</summary>
        public bool TryAcquireWindow()
        {
            if (_window != IntPtr.Zero) return true;
            if (_proc == null || _proc.HasExited) return false;
            _findResult = IntPtr.Zero;
            _findPid = (uint)_proc.Id;
            EnumWindows(Collect, IntPtr.Zero);
            if (_findResult != IntPtr.Zero) { _window = _findResult; return true; }
            return false;
        }

        /// <summary>Re-parents the window into the host, strips its frame, fits it.</summary>
        public bool EmbedInto(IntPtr host, int topOffset = 0, int bottomOffset = 0)
        {
            if (_window == IntPtr.Zero || host == IntPtr.Zero) return false;
            SetParent(_window, host);
            int style = GetWindowLong(_window, GWL_STYLE);
            SetWindowLong(_window, GWL_STYLE, (style & ~StyleStrip) | WS_CHILD | WS_VISIBLE);
            SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOMOVE | SWP_NOSIZE);
            FitTo(host, topOffset, bottomOffset);
            return GetParent(_window) == host;
        }

        /// <summary>Fits the embedded window to the host's client area between chrome offsets.</summary>
        public void FitTo(IntPtr host, int topOffset = 0, int bottomOffset = 0)
        {
            if (_window == IntPtr.Zero || host == IntPtr.Zero) return;
            if (GetClientRect(host, out RECT r))
                MoveWindow(_window, 0, topOffset, r.Right - r.Left, (r.Bottom - r.Top) - topOffset - bottomOffset, true);
        }

        /// <summary>
        /// Asks the emulator to close via its window (it saves battery/settings on a
        /// normal close); falls back to terminating after the timeout.
        /// </summary>
        public void CloseGracefully(int timeoutMs = 8000)
        {
            try
            {
                if (_proc == null || _proc.HasExited) return;
                if (_window != IntPtr.Zero) SendMessage(_window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (!_proc.WaitForExit(timeoutMs)) _proc.Kill();
            }
            catch (Exception ex) { Trace.WriteLine($"[MesenCE] close failed: {ex.Message}"); }
        }

        public void Dispose() { try { _proc?.Dispose(); } catch { } }
    }
}
