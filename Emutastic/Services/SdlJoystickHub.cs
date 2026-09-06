using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Emutastic.Services
{
    /// <summary>
    /// Reads live button/axis state from SDL3 for controllers that XInput cannot
    /// see — DirectInput / generic-HID pads such as the Retrolink SNES adapters,
    /// ShanWan / Hyperkin console adapters, arcade sticks, and pre-XInput USB
    /// gamepads.
    ///
    /// WHY SDL AND NOT dinput8.dll
    /// ---------------------------
    /// SDL3's Windows joystick backend already *is* DirectInput (alongside
    /// RawInput and Windows.Gaming.Input), and SDL3.dll already ships with
    /// Emutastic — <see cref="ControllerManager"/> has used it for device-name
    /// enumeration since v1.4.6. Everything needed for DirectInput coverage was
    /// therefore already loaded and initialised; only *state reading* was
    /// missing. Going through SDL also buys the built-in gamepad database, so a
    /// recognised pad reports a correct Xbox-shaped layout instead of arbitrary
    /// HID button indices.
    ///
    /// THREADING
    /// ---------
    /// Every SDL call in this class runs on the dedicated SDL thread owned by
    /// <see cref="ControllerManager"/> (an STA thread with its own dispatcher,
    /// pumping WM_DEVICECHANGE for hot-plug). That thread is allowed to stall:
    /// SDL_PumpEvents can block for 10-20 seconds while a complex controller
    /// hot-plugs. ControllerManager's 60 Hz poll timer must never wait on that,
    /// so this class never exposes a blocking call. Instead it publishes an
    /// immutable snapshot dictionary that poll threads read lock-free via
    /// <see cref="GetSnapshot"/>. A hot-plug stall freezes the last snapshot for
    /// its duration; it cannot stall input polling.
    ///
    /// AXIS CONVENTIONS
    /// ----------------
    /// Snapshots are published in *XInput* shape — an XInput-style button mask
    /// and thumb values where up is POSITIVE — because every consumer downstream
    /// of ControllerManager (the per-console mapping table, the analog helpers,
    /// the frontend chords) was written against that shape. SDL reports Y as
    /// down-positive, so <see cref="ToXInputThumb"/> negates it. Getting this
    /// backwards inverts every stick on every non-XInput pad, so the conversion
    /// lives in exactly one place.
    /// </summary>
    internal static class SdlJoystickHub
    {
        private const string SDL3Dll = "SDL3.dll";

        // ─────────────────────────────────────────────────────────────────────
        // P/Invoke
        //
        // Entry-point names are NOT verified by the compiler — a typo here
        // builds green and throws EntryPointNotFoundException at runtime, on the
        // SDL thread, where it would surface only as "controller does nothing".
        // Every name below is transcribed from SDL3's public headers
        // (include/SDL3/SDL_joystick.h and SDL_gamepad.h).
        //
        // Note SDL_JoystickConnected — it is NOT called SDL_GetJoystickConnected,
        // unlike its neighbours in the same header.
        //
        // Every bool-returning SDL3 function is marshalled as UnmanagedType.U1.
        // SDL3 returns a 1-byte C99 <stdbool.h> bool; .NET's default marshalling
        // for a bool return is the 4-byte Win32 BOOL, which reads undefined
        // upper bits of EAX and can yield false positives.
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_OpenJoystick(uint instance_id);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_CloseJoystick(IntPtr joystick);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_OpenGamepad(uint instance_id);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_CloseGamepad(IntPtr gamepad);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_UpdateJoysticks();

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_UpdateGamepads();

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_GetJoystickButton(IntPtr joystick, int button);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern short SDL_GetJoystickAxis(IntPtr joystick, int axis);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte SDL_GetJoystickHat(IntPtr joystick, int hat);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GetNumJoystickButtons(IntPtr joystick);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GetNumJoystickAxes(IntPtr joystick);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_GetNumJoystickHats(IntPtr joystick);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_JoystickConnected(IntPtr joystick);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_RumbleGamepad(IntPtr gamepad, ushort low, ushort high, uint duration_ms);

        [DllImport(SDL3Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SDL_RumbleJoystick(IntPtr joystick, ushort low, ushort high, uint duration_ms);

        // SDL_GamepadButton — header order, SOUTH == 0.
        private const int SDL_GAMEPAD_BUTTON_SOUTH          = 0;
        private const int SDL_GAMEPAD_BUTTON_EAST           = 1;
        private const int SDL_GAMEPAD_BUTTON_WEST           = 2;
        private const int SDL_GAMEPAD_BUTTON_NORTH          = 3;
        private const int SDL_GAMEPAD_BUTTON_BACK           = 4;
        private const int SDL_GAMEPAD_BUTTON_START          = 6;
        private const int SDL_GAMEPAD_BUTTON_LEFT_STICK     = 7;
        private const int SDL_GAMEPAD_BUTTON_RIGHT_STICK    = 8;
        private const int SDL_GAMEPAD_BUTTON_LEFT_SHOULDER  = 9;
        private const int SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER = 10;
        private const int SDL_GAMEPAD_BUTTON_DPAD_UP        = 11;
        private const int SDL_GAMEPAD_BUTTON_DPAD_DOWN      = 12;
        private const int SDL_GAMEPAD_BUTTON_DPAD_LEFT      = 13;
        private const int SDL_GAMEPAD_BUTTON_DPAD_RIGHT     = 14;

        // SDL_GamepadAxis — header order, LEFTX == 0.
        private const int SDL_GAMEPAD_AXIS_LEFTX         = 0;
        private const int SDL_GAMEPAD_AXIS_LEFTY         = 1;
        private const int SDL_GAMEPAD_AXIS_RIGHTX        = 2;
        private const int SDL_GAMEPAD_AXIS_RIGHTY        = 3;
        private const int SDL_GAMEPAD_AXIS_LEFT_TRIGGER  = 4;
        private const int SDL_GAMEPAD_AXIS_RIGHT_TRIGGER = 5;

        private const byte SDL_HAT_UP    = 0x01;
        private const byte SDL_HAT_RIGHT = 0x02;
        private const byte SDL_HAT_DOWN  = 0x04;
        private const byte SDL_HAT_LEFT  = 0x08;

        // XInput button masks, duplicated here so the hub does not depend on
        // ControllerManager's private constants. Snapshots are published in this
        // shape (see the class remarks).
        private const ushort XI_DPAD_UP        = 0x0001;
        private const ushort XI_DPAD_DOWN      = 0x0002;
        private const ushort XI_DPAD_LEFT      = 0x0004;
        private const ushort XI_DPAD_RIGHT     = 0x0008;
        private const ushort XI_START          = 0x0010;
        private const ushort XI_BACK           = 0x0020;
        private const ushort XI_LEFT_THUMB     = 0x0040;
        private const ushort XI_RIGHT_THUMB    = 0x0080;
        private const ushort XI_LEFT_SHOULDER  = 0x0100;
        private const ushort XI_RIGHT_SHOULDER = 0x0200;
        private const ushort XI_A              = 0x1000;
        private const ushort XI_B              = 0x2000;
        private const ushort XI_X              = 0x4000;
        private const ushort XI_Y              = 0x8000;

        // ─────────────────────────────────────────────────────────────────────
        // Published state
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Immutable per-device state, published as a set by the SDL thread and
        /// read lock-free by 60 Hz poll threads. Never mutated after publish.
        /// </summary>
        internal sealed class Snapshot
        {
            public string DeviceId   = "";
            public string Name       = "";
            public bool   IsGamepad;
            public bool   Connected;

            /// <summary>XInput-shaped button mask (XI_* constants).</summary>
            public ushort Buttons;

            /// <summary>Thumb axes in XInput convention: up is POSITIVE.</summary>
            public short  LeftX, LeftY, RightX, RightY;

            /// <summary>Triggers as 0..255, matching XINPUT_GAMEPAD.</summary>
            public byte   LeftTrigger, RightTrigger;
        }

        // Swapped wholesale by the SDL thread; readers take the reference once.
        private static Dictionary<string, Snapshot> _snapshots = new();

        /// <summary>
        /// Latest state for <paramref name="deviceId"/>, or null if that device
        /// is not currently open. Lock-free and non-blocking — safe to call from
        /// a 60 Hz poll timer while the SDL thread is stalled in a hot-plug.
        /// </summary>
        internal static Snapshot? GetSnapshot(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            var map = Volatile.Read(ref _snapshots);
            return map.TryGetValue(deviceId, out var s) ? s : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Open devices — touched ONLY on the SDL thread
        // ─────────────────────────────────────────────────────────────────────

        private sealed class OpenDevice
        {
            public string  DeviceId = "";
            public string  Name     = "";
            public uint    InstanceId;
            public IntPtr  Joystick;      // always opened
            public IntPtr  Gamepad;       // IntPtr.Zero when SDL has no mapping
            public int     NumButtons, NumAxes, NumHats;
        }

        private static readonly Dictionary<string, OpenDevice> _open = new();

        /// <summary>
        /// Builds the stable per-device id used by configuration and snapshots.
        ///
        /// SDL's joystick GUID encodes bus/vendor/product but is identical for
        /// two units of the same model — which is precisely the reported case
        /// ("Retrolink SNES controller" listed twice). The occurrence index
        /// disambiguates them deterministically by enumeration order, the same
        /// approach RetroArch uses. Name is used rather than the GUID because
        /// SDL_GetJoystickGUIDForID returns a 16-byte struct by value, which is
        /// an avoidable P/Invoke marshalling hazard.
        ///
        /// Unplugging the first of two identical pads renumbers the second; that
        /// is inherent to any index-based scheme and matches other frontends.
        /// </summary>
        internal static string MakeDeviceId(string name, int occurrence) => $"{name}#{occurrence}";

        /// <summary>
        /// Reconciles open handles against the currently-connected device list.
        /// MUST be called on the SDL thread. <paramref name="devices"/> is the
        /// enumeration in SDL order, as (instanceId, name) pairs.
        /// </summary>
        internal static void ReconcileOnSdlThread(IReadOnlyList<(uint InstanceId, string Name)> devices)
        {
            var wanted = new Dictionary<string, (uint InstanceId, string Name)>();
            var seen   = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (instanceId, name) in devices)
            {
                seen.TryGetValue(name, out int n);
                seen[name] = n + 1;
                wanted[MakeDeviceId(name, n)] = (instanceId, name);
            }

            // Close devices that vanished, or whose instance id changed (a
            // replug reuses the id slot but yields a new SDL instance).
            foreach (var id in _open.Keys.ToList())
            {
                if (wanted.TryGetValue(id, out var w) && w.InstanceId == _open[id].InstanceId)
                    continue;
                CloseDevice(_open[id]);
                _open.Remove(id);
            }

            foreach (var (id, w) in wanted)
            {
                if (_open.ContainsKey(id)) continue;

                IntPtr joy = IntPtr.Zero, pad = IntPtr.Zero;
                try
                {
                    joy = SDL_OpenJoystick(w.InstanceId);
                    if (joy == IntPtr.Zero)
                    {
                        ControllerManager.CtrlLog($"SdlJoystickHub: SDL_OpenJoystick failed for '{id}' (instance {w.InstanceId})");
                        continue;
                    }

                    // A gamepad handle gives SDL's database mapping (correct
                    // Xbox-shaped layout). Absent one, we fall back to reading
                    // the raw joystick positionally.
                    pad = SDL_OpenGamepad(w.InstanceId);

                    var dev = new OpenDevice
                    {
                        DeviceId   = id,
                        Name       = w.Name,
                        InstanceId = w.InstanceId,
                        Joystick   = joy,
                        Gamepad    = pad,
                        NumButtons = SDL_GetNumJoystickButtons(joy),
                        NumAxes    = SDL_GetNumJoystickAxes(joy),
                        NumHats    = SDL_GetNumJoystickHats(joy),
                    };
                    _open[id] = dev;
                    ControllerManager.CtrlLog(
                        $"SdlJoystickHub: opened '{id}' instance={w.InstanceId} " +
                        $"mapped={(pad != IntPtr.Zero ? "gamepad" : "raw-joystick")} " +
                        $"buttons={dev.NumButtons} axes={dev.NumAxes} hats={dev.NumHats}");
                }
                catch (Exception ex)
                {
                    ControllerManager.CtrlLog($"SdlJoystickHub: open '{id}' THREW {ex.GetType().Name}: {ex.Message}");
                    try { if (pad != IntPtr.Zero) SDL_CloseGamepad(pad); } catch { }
                    try { if (joy != IntPtr.Zero) SDL_CloseJoystick(joy); } catch { }
                }
            }
        }

        private static void CloseDevice(OpenDevice dev)
        {
            try { if (dev.Gamepad  != IntPtr.Zero) SDL_CloseGamepad(dev.Gamepad); }   catch { }
            try { if (dev.Joystick != IntPtr.Zero) SDL_CloseJoystick(dev.Joystick); } catch { }
            ControllerManager.CtrlLog($"SdlJoystickHub: closed '{dev.DeviceId}'");
        }

        /// <summary>
        /// Reads every open device and publishes a fresh snapshot set. MUST be
        /// called on the SDL thread; drive it at ~60 Hz.
        /// </summary>
        internal static void PollOnSdlThread()
        {
            if (_open.Count == 0)
            {
                // Nothing open — publish an empty set once rather than every
                // tick, so readers see "no devices" without churning garbage.
                if (Volatile.Read(ref _snapshots).Count != 0)
                    Volatile.Write(ref _snapshots, new Dictionary<string, Snapshot>());
                return;
            }

            try
            {
                SDL_UpdateJoysticks();
                SDL_UpdateGamepads();
            }
            catch (Exception ex)
            {
                ControllerManager.CtrlLog($"SdlJoystickHub: update THREW {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var fresh = new Dictionary<string, Snapshot>(_open.Count, StringComparer.Ordinal);
            foreach (var dev in _open.Values)
            {
                try
                {
                    fresh[dev.DeviceId] = dev.Gamepad != IntPtr.Zero
                        ? ReadGamepad(dev)
                        : ReadRawJoystick(dev);
                }
                catch (Exception ex)
                {
                    ControllerManager.CtrlLog($"SdlJoystickHub: read '{dev.DeviceId}' THREW {ex.GetType().Name}: {ex.Message}");
                }
            }
            Volatile.Write(ref _snapshots, fresh);
        }

        /// <summary>
        /// SDL has a database mapping for this pad — read it in normalised
        /// gamepad terms and re-express as XInput. This is the path an Xbox
        /// pad, a DualSense, or any pad in SDL's gamepad DB takes.
        /// </summary>
        private static Snapshot ReadGamepad(OpenDevice dev)
        {
            IntPtr g = dev.Gamepad;
            ushort b = 0;

            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_SOUTH))          b |= XI_A;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_EAST))           b |= XI_B;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_WEST))           b |= XI_X;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_NORTH))          b |= XI_Y;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_BACK))           b |= XI_BACK;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_START))          b |= XI_START;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_LEFT_STICK))     b |= XI_LEFT_THUMB;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_RIGHT_STICK))    b |= XI_RIGHT_THUMB;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_LEFT_SHOULDER))  b |= XI_LEFT_SHOULDER;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER)) b |= XI_RIGHT_SHOULDER;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_DPAD_UP))        b |= XI_DPAD_UP;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_DPAD_DOWN))      b |= XI_DPAD_DOWN;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_DPAD_LEFT))      b |= XI_DPAD_LEFT;
            if (SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_DPAD_RIGHT))     b |= XI_DPAD_RIGHT;

            return new Snapshot
            {
                DeviceId  = dev.DeviceId,
                Name      = dev.Name,
                IsGamepad = true,
                Connected = SDL_JoystickConnected(dev.Joystick),
                Buttons   = b,
                LeftX     =  SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_LEFTX),
                LeftY     = ToXInputThumb(SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_LEFTY)),
                RightX    =  SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_RIGHTX),
                RightY    = ToXInputThumb(SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_RIGHTY)),
                // SDL reports triggers as 0..32767; XINPUT_GAMEPAD wants 0..255.
                LeftTrigger  = ToXInputTrigger(SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_LEFT_TRIGGER)),
                RightTrigger = ToXInputTrigger(SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)),
            };
        }

        /// <summary>
        /// No SDL mapping exists for this device (the Retrolink / ShanWan case).
        /// Read the raw HID report positionally.
        ///
        /// The order below — buttons 0..3 as the face cluster, 4/5 as shoulders,
        /// 6/7 as select/start, 8/9 as stick clicks, hat 0 as the d-pad, axes
        /// 0..3 as the two sticks — is the conventional DirectInput gamepad
        /// layout and is what a dinput8 implementation would have surfaced
        /// anyway. It only has to be deterministic: users rebind everything in
        /// Preferences → Input regardless.
        /// </summary>
        private static Snapshot ReadRawJoystick(OpenDevice dev)
        {
            IntPtr j = dev.Joystick;
            ushort b = 0;

            bool Btn(int i) => i < dev.NumButtons && SDL_GetJoystickButton(j, i);

            if (Btn(0)) b |= XI_A;
            if (Btn(1)) b |= XI_B;
            if (Btn(2)) b |= XI_X;
            if (Btn(3)) b |= XI_Y;
            if (Btn(4)) b |= XI_LEFT_SHOULDER;
            if (Btn(5)) b |= XI_RIGHT_SHOULDER;
            if (Btn(6)) b |= XI_BACK;
            if (Btn(7)) b |= XI_START;
            if (Btn(8)) b |= XI_LEFT_THUMB;
            if (Btn(9)) b |= XI_RIGHT_THUMB;

            // D-pad: most HID pads report it as hat 0. Pads that instead wire
            // the d-pad to buttons keep working through the face-button path
            // plus user remapping.
            if (dev.NumHats > 0)
            {
                byte hat = SDL_GetJoystickHat(j, 0);
                if ((hat & SDL_HAT_UP)    != 0) b |= XI_DPAD_UP;
                if ((hat & SDL_HAT_DOWN)  != 0) b |= XI_DPAD_DOWN;
                if ((hat & SDL_HAT_LEFT)  != 0) b |= XI_DPAD_LEFT;
                if ((hat & SDL_HAT_RIGHT) != 0) b |= XI_DPAD_RIGHT;
            }

            short Axis(int i) => i < dev.NumAxes ? SDL_GetJoystickAxis(j, i) : (short)0;

            return new Snapshot
            {
                DeviceId  = dev.DeviceId,
                Name      = dev.Name,
                IsGamepad = false,
                Connected = SDL_JoystickConnected(j),
                Buttons   = b,
                LeftX     =  Axis(0),
                LeftY     = ToXInputThumb(Axis(1)),
                RightX    =  Axis(2),
                RightY    = ToXInputThumb(Axis(3)),
                // A raw HID pad has no analog triggers to read. Digital
                // shoulder buttons already landed in the mask above.
                LeftTrigger  = 0,
                RightTrigger = 0,
            };
        }

        /// <summary>
        /// SDL reports a stick's Y axis as down-positive; XInput reports it as
        /// up-positive, and every consumer downstream of ControllerManager
        /// assumes XInput. Negation is the whole conversion — but short.MinValue
        /// has no positive counterpart, so it is clamped first. Without the
        /// clamp, holding a stick fully up would wrap to fully down.
        /// </summary>
        private static short ToXInputThumb(short sdlValue) =>
            (short)-Math.Max((int)sdlValue, -short.MaxValue);

        /// <summary>SDL trigger range (0..32767) to XINPUT_GAMEPAD's (0..255).</summary>
        private static byte ToXInputTrigger(short sdlValue) =>
            sdlValue <= 0 ? (byte)0 : (byte)Math.Min(255, sdlValue * 255 / short.MaxValue);

        /// <summary>
        /// Rumble a device by id. MUST be called on the SDL thread. Prefers the
        /// gamepad handle so SDL applies any per-device motor quirks; falls back
        /// to the raw joystick. Most DirectInput-era pads have no motors, in
        /// which case SDL returns false and this is a silent no-op.
        /// </summary>
        internal static void RumbleOnSdlThread(string deviceId, ushort low, ushort high, uint durationMs)
        {
            if (!_open.TryGetValue(deviceId, out var dev)) return;
            try
            {
                if (dev.Gamepad != IntPtr.Zero) SDL_RumbleGamepad(dev.Gamepad, low, high, durationMs);
                else                            SDL_RumbleJoystick(dev.Joystick, low, high, durationMs);
            }
            catch { }
        }

        /// <summary>
        /// Release every open handle. MUST be called on the SDL thread, during
        /// app shutdown, before the dispatcher stops.
        /// </summary>
        internal static void ShutdownOnSdlThread()
        {
            foreach (var dev in _open.Values) CloseDevice(dev);
            _open.Clear();
            Volatile.Write(ref _snapshots, new Dictionary<string, Snapshot>());
        }
    }
}
