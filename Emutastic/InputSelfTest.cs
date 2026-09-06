using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Emutastic.Services;

namespace Emutastic
{
    /// <summary>
    /// Headless self-test for controller → player routing:
    /// <c>Emutastic.exe --selftest-input [report.log] [--portable]</c>.
    ///
    /// Uses SDL3 virtual joysticks, so it proves the whole SDL read path with no hardware
    /// attached and no window: enumeration, stable "name#occurrence" ids and the "(2)" display
    /// suffix, the raw positional layout with its trigger and d-pad rules, the XInput-shaped
    /// snapshot (including the Y-axis sign flip), and <see cref="ControllerPortTable"/>'s three
    /// port-resolution rules, the disconnect rule among them. Real controllers that ARE attached
    /// are listed for information and left alone. Never reads or writes the user's configuration.
    ///
    /// Everything runs on the calling thread, which is therefore "the SDL thread" for the
    /// duration — the app's own SDL thread (ControllerManager.EnsureSdl3InitInBackground) is
    /// never started in this mode, and no ControllerManager instance is created. Exit code 0 =
    /// every check passed. Output goes to the report file and, when launched from a terminal,
    /// to that terminal too (a WinExe has no console of its own).
    ///
    /// Port of the Linux build's InputSelfTest. The checks mirror it except where the two read
    /// paths deliberately differ: this one publishes XInput conventions (stick up is POSITIVE,
    /// triggers are 0..255), the Linux one passes SDL's values straight through.
    /// </summary>
    internal static class InputSelfTest
    {
        private const string SDL3Dll = "SDL3.dll";
        private const uint SDL_INIT_JOYSTICK = 0x00000200, SDL_INIT_GAMEPAD = 0x00002000;
        private const ushort SDL_JOYSTICK_TYPE_UNKNOWN = 0, SDL_JOYSTICK_TYPE_GAMEPAD = 1;
        private const byte SDL_HAT_UP = 0x01;

        // SDL_VirtualJoystickDesc, field-for-field from SDL3/SDL_joystick.h. Sequential layout
        // with natural alignment gives 136 bytes on x64; SDL validates `version` against it.
        [StructLayout(LayoutKind.Sequential)]
        private struct SDL_VirtualJoystickDesc
        {
            public uint   version;
            public ushort type;
            public ushort padding;
            public ushort vendor_id;
            public ushort product_id;
            public ushort naxes;
            public ushort nbuttons;
            public ushort nballs;
            public ushort nhats;
            public ushort ntouchpads;
            public ushort nsensors;
            public ushort padding2_0;
            public ushort padding2_1;
            public uint   button_mask;
            public uint   axis_mask;
            public IntPtr name;
            public IntPtr touchpads;
            public IntPtr sensors;
            public IntPtr userdata;
            public IntPtr Update;
            public IntPtr SetPlayerIndex;
            public IntPtr Rumble;
            public IntPtr RumbleTriggers;
            public IntPtr SetLED;
            public IntPtr SendEffect;
            public IntPtr SetSensorsEnabled;
            public IntPtr Cleanup;
        }

        // SDL3 returns 1-byte C99 bools — pinned to U1 like every other SDL3 P/Invoke here.
        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_InitSubSystem(uint flags);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_QuitSubSystem(uint flags);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint SDL_AttachVirtualJoystick(ref SDL_VirtualJoystickDesc desc);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_DetachVirtualJoystick(uint instance_id);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetJoystickFromID(uint instance_id);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_SetJoystickVirtualButton(IntPtr joystick, int button, [MarshalAs(UnmanagedType.U1)] bool down);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_SetJoystickVirtualAxis(IntPtr joystick, int axis, short value);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_SetJoystickVirtualHat(IntPtr joystick, int hat, byte value);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(uint dwProcessId);
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        private static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

        // ── Output: the report file, plus the parent terminal when there is one ─────────────
        private sealed class Report : IDisposable
        {
            private readonly StreamWriter? _file;
            private readonly StreamWriter? _console;
            public int Failures;

            public Report(string path)
            {
                try
                {
                    // A WinExe launched from cmd / PowerShell can borrow the parent's console;
                    // Console.Out was bound before that console existed, so re-open stdout.
                    if (AttachConsole(ATTACH_PARENT_PROCESS))
                        _console = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                }
                catch { }
                try
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _file = new StreamWriter(path, append: false) { AutoFlush = true };
                }
                catch { }
            }

            public void Line(string text)
            {
                try { _console?.WriteLine(text); } catch { }
                try { _file?.WriteLine(text); }    catch { }
            }

            public void Check(bool ok, string what)
            {
                Line($"  [{(ok ? "PASS" : "FAIL")}] {what}");
                if (!ok) Failures++;
            }

            public void Dispose()
            {
                try { _file?.Dispose(); }    catch { }
                try { _console?.Dispose(); } catch { }
            }
        }

        // ── One virtual joystick ──────────────────────────────────────────────────────────────
        private sealed class Virtual : IDisposable
        {
            public uint Id;
            private IntPtr _joystick;
            private readonly IntPtr _name;
            private readonly Report _r;

            public Virtual(Report r, string name, ushort type, ushort axes, ushort buttons, ushort hats)
            {
                _r    = r;
                _name = Marshal.StringToCoTaskMemUTF8(name);
                var desc = new SDL_VirtualJoystickDesc
                {
                    version  = (uint)Marshal.SizeOf<SDL_VirtualJoystickDesc>(),
                    type     = type,
                    naxes    = axes,
                    nbuttons = buttons,
                    nhats    = hats,
                    name     = _name,
                };
                Id = SDL_AttachVirtualJoystick(ref desc);
                if (Id == 0) throw new InvalidOperationException($"SDL_AttachVirtualJoystick('{name}') failed: {Err()}");
                // The joystick must be OPEN for SDL_SetJoystickVirtual* to accept it.
                // SdlJoystickHub opens it on reconcile; SDL_GetJoystickFromID returns that handle.
            }

            private IntPtr Handle => _joystick != IntPtr.Zero ? _joystick : (_joystick = SDL_GetJoystickFromID(Id));

            public void Button(int b, bool down) { if (!SDL_SetJoystickVirtualButton(Handle, b, down)) _r.Line($"    (virtual button set failed: {Err()})"); }
            public void Axis(int a, short v)     { if (!SDL_SetJoystickVirtualAxis(Handle, a, v))     _r.Line($"    (virtual axis set failed: {Err()})"); }
            public void Hat(int h, byte v)       { if (!SDL_SetJoystickVirtualHat(Handle, h, v))      _r.Line($"    (virtual hat set failed: {Err()})"); }

            public void Detach()
            {
                if (Id == 0) return;
                SDL_DetachVirtualJoystick(Id);
                Id = 0;
                _joystick = IntPtr.Zero;
            }

            public void Dispose()
            {
                Detach();
                if (_name != IntPtr.Zero) Marshal.FreeCoTaskMem(_name);
            }
        }

        // ── Driving the hub the way its SDL thread does ───────────────────────────────────────
        // Refresh = what RefreshDevicesCacheOnSdlThread does once a second: pump, enumerate,
        // reconcile handles, then read. Pump = three 16 ms ticks of the state poll; virtual
        // state changes become visible to readers on the next joystick update.
        private static List<ControllerManager.ControllerDevice> Refresh()
        {
            var raw = SdlJoystickHub.EnumerateOnSdlThread();
            SdlJoystickHub.ReconcileOnSdlThread(raw);
            Pump();
            return ControllerManager.BuildDeviceRecords(raw);
        }

        private static void Pump()
        {
            for (int i = 0; i < 3; i++) SdlJoystickHub.PollOnSdlThread();
        }

        private static SdlJoystickHub.Snapshot? Snap(string id) => SdlJoystickHub.Current.Get(id);

        private static bool Btn(string id, ushort mask)
        {
            var s = Snap(id);
            return s != null && (s.Buttons & mask) != 0;
        }

        private static string? Port(ControllerPortTable table, int port) => table.DeviceFor(port, SdlJoystickHub.Current);

        private static string DefaultReportPath()
        {
            try { return Path.Combine(AppPaths.GetFolder("Logs"), "input-selftest.log"); }
            catch { return Path.Combine(AppContext.BaseDirectory, "input-selftest.log"); }
        }

        /// <summary>Runs every check. Returns the process exit code: 0 = all passed.</summary>
        public static int Run(string? reportPath)
        {
            string path = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportPath() : reportPath!;
            using var r = new Report(path);
            r.Line("=== input self-test: controller -> player routing ===");
            r.Line($"report: {path}");

            int descSize = Marshal.SizeOf<SDL_VirtualJoystickDesc>();
            r.Check(descSize == 136, $"SDL_VirtualJoystickDesc marshals to 136 bytes (got {descSize})");

            bool sdlUp;
            try
            {
                sdlUp = SDL_InitSubSystem(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD);
            }
            catch (DllNotFoundException)
            {
                r.Line("  [FAIL] SDL3.dll not found — download it from Preferences → Extras, or place it next to Emutastic.exe");
                r.Line("=== FAIL ===");
                return 1;
            }
            if (!sdlUp)
            {
                r.Line($"  [FAIL] SDL_InitSubSystem: {Err()}");
                r.Line("=== FAIL ===");
                return 1;
            }

            try
            {
                RunChecks(r);
            }
            catch (Exception ex)
            {
                r.Line($"  [FAIL] unhandled {ex.GetType().Name}: {ex.Message}");
                r.Failures++;
            }
            finally
            {
                try { SdlJoystickHub.ShutdownOnSdlThread(); } catch { }
                try { SDL_QuitSubSystem(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD); } catch { }
            }

            r.Line(r.Failures == 0 ? "=== PASS ===" : $"=== FAIL ({r.Failures} check(s)) ===");
            return r.Failures == 0 ? 0 : 1;
        }

        private static void RunChecks(Report r)
        {
            const ushort A = ControllerManager.RAW_A, DPAD_UP = ControllerManager.RAW_DPAD_UP;

            Refresh();
            var real = SdlJoystickHub.Current;
            int realCount = real.Order.Length;
            r.Line($"--- real controllers attached right now: {realCount}");
            foreach (var id in real.Order)
            {
                var s = real.ById[id];
                r.Line($"    \"{id}\"  {(s.IsGamepad ? "gamepad-mapped" : "RAW joystick")}  buttons={s.NumButtons} axes={s.NumAxes} hats={s.NumHats}");
            }

            // Two IDENTICAL virtual gamepads (tests the "#0"/"#1" occurrence ids) and one virtual
            // joystick SDL has no mapping for (tests the raw positional path).
            using var padA = new Virtual(r, "Virtual Pad", SDL_JOYSTICK_TYPE_GAMEPAD, axes: 6, buttons: 21, hats: 0);
            using var padB = new Virtual(r, "Virtual Pad", SDL_JOYSTICK_TYPE_GAMEPAD, axes: 6, buttons: 21, hats: 0);
            using var raw  = new Virtual(r, "Virtual Raw Stick", SDL_JOYSTICK_TYPE_UNKNOWN, axes: 4, buttons: 12, hats: 1);
            var records = Refresh();
            var set = SdlJoystickHub.Current;

            r.Line("--- enumeration + identity");
            r.Check(set.Order.Length == realCount + 3, $"three virtual devices enumerated (total {set.Order.Length})");
            r.Check(set.ById.ContainsKey("Virtual Pad#0") && set.ById.ContainsKey("Virtual Pad#1"),
                    "identical pads get ids \"Virtual Pad#0\" and \"Virtual Pad#1\"");
            r.Check(records.Any(d => d.Id == "Virtual Pad#1" && d.DisplayName == "Virtual Pad (2)"),
                    "the second identical pad displays as \"Virtual Pad (2)\"");
            r.Check(records.Any(d => d.Id == "Virtual Pad#0" && d.DisplayName == "Virtual Pad"),
                    "…and the first keeps its plain name");
            r.Check(set.Get("Virtual Raw Stick#0") is { IsGamepad: false },
                    "the unmapped joystick is enumerated and flagged RAW (no gamepad mapping)");
            r.Check(set.Get("Virtual Pad#0") is { IsGamepad: true },
                    "a virtual gamepad-type joystick gets an SDL gamepad mapping");

            // Bind P1 -> the raw stick, P2 -> the SECOND identical pad. P3/P4 unbound.
            var table = new ControllerPortTable("selftest");
            table.SetBinding(0, "Virtual Raw Stick#0");
            table.SetBinding(1, "Virtual Pad#1");

            r.Line("--- port resolution");
            r.Check(Port(table, 0) == "Virtual Raw Stick#0", $"P1 reads its bound raw stick (got {Port(table, 0) ?? "none"})");
            r.Check(Port(table, 1) == "Virtual Pad#1",       $"P2 reads its bound pad #1 (got {Port(table, 1) ?? "none"})");
            // P3 is unbound: it takes the first UNCLAIMED device in enumeration order. With no real
            // pads that is "Virtual Pad#0"; with real pads attached it is the first real one.
            string? p3 = Port(table, 2);
            r.Check(p3 != null && p3 != "Virtual Raw Stick#0" && p3 != "Virtual Pad#1",
                    $"P3 (unbound) takes an unclaimed device, never a bound one (got {p3 ?? "none"})");
            if (realCount == 0)
            {
                r.Check(p3 == "Virtual Pad#0", "P3 defaults to \"Virtual Pad#0\" (first unclaimed, enumeration order)");
                r.Check(Port(table, 3) == null, "P4 (unbound) reads nothing: no unclaimed pad is left");
            }

            r.Line("--- raw joystick reads on P1 (positional layout, published in XInput shape)");
            raw.Button(0, true); Pump();
            r.Check(Btn("Virtual Raw Stick#0", A), "raw button 0 -> A on the stick");
            r.Check(!Btn("Virtual Pad#1", A),      "…and NOT on P2's pad");
            raw.Button(0, false);
            raw.Hat(0, SDL_HAT_UP); Pump();
            r.Check(Btn("Virtual Raw Stick#0", DPAD_UP), "raw hat UP -> D-pad UP");
            raw.Hat(0, 0);
            raw.Button(10, true); Pump();
            r.Check(Snap("Virtual Raw Stick#0")?.LeftTrigger == 255, "raw spare button 10 -> L2 (triggers claimed first)");
            raw.Button(10, false);
            raw.Button(11, true); Pump();
            r.Check(Snap("Virtual Raw Stick#0")?.RightTrigger == 255, "raw spare button 11 -> R2");
            raw.Button(11, false);
            raw.Axis(1, -30000); Pump();
            r.Check(Snap("Virtual Raw Stick#0")?.LeftY > 20000,
                    "raw axis 1 pushed negative (SDL: up) publishes as POSITIVE LeftY (XInput: up) — the one place the sign flips");
            raw.Axis(1, short.MinValue); Pump();
            r.Check(Snap("Virtual Raw Stick#0")?.LeftY == short.MaxValue,
                    "…and full deflection clamps to +32767 instead of wrapping to fully-down");
            raw.Axis(1, 0); Pump();

            r.Line("--- gamepad reads on P2 (SDL mapping)");
            padB.Button(0, true); Pump();   // SDL_GAMEPAD_BUTTON_SOUTH
            r.Check(Btn("Virtual Pad#1", A),  "pad #1 SOUTH -> A on P2's pad");
            r.Check(!Btn("Virtual Pad#0", A), "…and NOT on pad #0");
            padB.Button(0, false);
            padB.Axis(4, 30000); Pump();    // SDL_GAMEPAD_AXIS_LEFT_TRIGGER
            r.Check(Snap("Virtual Pad#1")?.LeftTrigger >= 200, "pad #1 left trigger axis -> LeftTrigger in XINPUT's 0..255");
            padB.Axis(4, 0); Pump();

            r.Line("--- hat-less raw pads: where the d-pad comes from (the Retrolink-class layouts)");
            // Cheap SNES/NES USB adapters report no hat and put the d-pad on axes 0/1; a button-only
            // board reports neither hat nor axes. The spare-button d-pad must serve ONLY the latter.
            using var adapter     = new Virtual(r, "Virtual Adapter",      SDL_JOYSTICK_TYPE_UNKNOWN, axes: 2, buttons: 14, hats: 0);
            using var buttonsOnly = new Virtual(r, "Virtual Buttons Only", SDL_JOYSTICK_TYPE_UNKNOWN, axes: 0, buttons: 16, hats: 0);
            Refresh();
            string? p3Held = Port(table, 2);
            table.SetBinding(2, "Virtual Adapter#0");
            table.SetBinding(3, "Virtual Buttons Only#0");
            r.Check(Port(table, 2) == "Virtual Adapter#0" && Port(table, 3) == "Virtual Buttons Only#0",
                    $"live rebind: P3 gives up its default pad ({p3Held ?? "none"}) for the adapter, P4 takes the button board");
            adapter.Button(12, true); Pump();
            r.Check(!Btn("Virtual Adapter#0", DPAD_UP), "adapter (no hat, HAS axes): spare button 12 is NOT a phantom UP");
            adapter.Button(12, false);
            adapter.Axis(1, -30000); Pump();
            r.Check(Snap("Virtual Adapter#0")?.LeftY > 20000, "…its axis 1 negative reaches the left stick (the stick->d-pad promotion reads it in-game)");
            adapter.Axis(1, 0);
            buttonsOnly.Button(12, true); Pump();
            r.Check(Btn("Virtual Buttons Only#0", DPAD_UP), "button-only board (no hat, no axes): spare button 12 -> UP");
            buttonsOnly.Button(12, false);
            buttonsOnly.Button(10, true); Pump();
            r.Check(Snap("Virtual Buttons Only#0")?.LeftTrigger == 255, "…and spare button 10 -> L2 (triggers are claimed before the d-pad)");
            buttonsOnly.Button(10, false);
            adapter.Detach(); buttonsOnly.Detach();
            Refresh();
            r.Check(Port(table, 2) == null && Port(table, 3) == null, "unplugging both bound pads leaves P3/P4 reading nothing");
            table.SetBinding(2, null);
            table.SetBinding(3, null);
            r.Check(Port(table, 2) == p3Held, $"unbinding P3 hands it its default pad back ({p3Held ?? "none"})");

            r.Line("--- disconnect rule: losing one player's pad must not shift the others");
            string? p2Before = Port(table, 1), p3Before = Port(table, 2);
            raw.Detach();
            Pump();   // no reconcile yet: the handle is still open, SDL reports it disconnected
            r.Check(Port(table, 0) == null, "P1's bound stick unplugged -> P1 reads nothing at once (before the 1 s reconcile)");
            Refresh();
            r.Check(Port(table, 0) == null,     "…and still nothing after the reconcile (not handed another pad)");
            r.Check(Port(table, 1) == p2Before, $"P2 unchanged ({p2Before ?? "none"})");
            r.Check(Port(table, 2) == p3Before, $"P3 unchanged ({p3Before ?? "none"})");
            using var raw2 = new Virtual(r, "Virtual Raw Stick", SDL_JOYSTICK_TYPE_UNKNOWN, axes: 4, buttons: 12, hats: 1);
            Refresh();
            r.Check(Port(table, 0) == "Virtual Raw Stick#0", "re-plugging the stick re-binds P1 without any config change");
            r.Check(Port(table, 1) == p2Before && Port(table, 2) == p3Before, "…and P2/P3 still unchanged");

            r.Line("--- documented limit: identical pads renumber when an earlier one leaves");
            padA.Detach();
            Refresh();
            r.Line($"    after unplugging \"Virtual Pad#0\": P2 -> {Port(table, 1) ?? "none"} (bound \"Virtual Pad#1\", which is now labelled #0 — inherent to any index scheme)");

            padB.Detach(); raw2.Detach();
            Refresh();
            r.Check(SdlJoystickHub.Current.Order.Length == realCount, "all virtual devices detached cleanly");
        }
    }
}
