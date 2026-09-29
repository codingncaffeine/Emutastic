using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace Emutastic.Services
{
    /// <summary>
    /// Player 1's keyboard: the keys built into the game window, with the binds
    /// saved in Preferences → Controls laid over them. The game window and
    /// Preferences both resolve through <see cref="Resolve"/>, so the key a row
    /// shows is the key that plays it.
    ///
    /// Targets are <see cref="LibretroInput"/> ids: 0-15 RetroPad buttons,
    /// 16-23 stick directions.
    ///
    /// A saved bind takes its key away from any built-in use, and retires the
    /// built-in keys of the button it binds: rebinding B from Z to Space leaves
    /// Z doing nothing, as in any other emulator. Buttons nobody rebound keep
    /// their built-in keys.
    /// </summary>
    public static class KeyboardBindings
    {
        // The built-in keys, in the order a row lists them (a row shows the
        // first key that reaches its target). WASD drives the left stick on
        // consoles that have one and the d-pad elsewhere; IJKL is the right
        // stick everywhere.
        private static readonly Key[] BuiltInOrder =
        {
            Key.Up, Key.Down, Key.Left, Key.Right,
            Key.W, Key.S, Key.A, Key.D,
            Key.Z, Key.X, Key.C, Key.V, Key.Q, Key.E,
            Key.Return, Key.RightShift, Key.LeftShift,
            Key.I, Key.K, Key.J, Key.L,
        };

        /// <summary>The target <paramref name="key"/> presses when nothing is
        /// bound over it, or <c>uint.MaxValue</c> for a key with no built-in use.</summary>
        public static uint BuiltInTarget(Key key, bool analogStick) => key switch
        {
            Key.Up     => LibretroInput.JOYPAD_UP,
            Key.Down   => LibretroInput.JOYPAD_DOWN,
            Key.Left   => LibretroInput.JOYPAD_LEFT,
            Key.Right  => LibretroInput.JOYPAD_RIGHT,
            Key.W      => analogStick ? LibretroInput.ANALOG_LEFT_UP    : LibretroInput.JOYPAD_UP,
            Key.S      => analogStick ? LibretroInput.ANALOG_LEFT_DOWN  : LibretroInput.JOYPAD_DOWN,
            Key.A      => analogStick ? LibretroInput.ANALOG_LEFT_LEFT  : LibretroInput.JOYPAD_LEFT,
            Key.D      => analogStick ? LibretroInput.ANALOG_LEFT_RIGHT : LibretroInput.JOYPAD_RIGHT,
            Key.Z      => LibretroInput.JOYPAD_B,
            Key.X      => LibretroInput.JOYPAD_A,
            Key.C      => LibretroInput.JOYPAD_Y,
            Key.V      => LibretroInput.JOYPAD_X,
            Key.Q      => LibretroInput.JOYPAD_L,
            Key.E      => LibretroInput.JOYPAD_R,
            Key.Return => LibretroInput.JOYPAD_START,
            Key.LeftShift or Key.RightShift => LibretroInput.JOYPAD_SELECT,
            Key.I      => LibretroInput.ANALOG_RIGHT_UP,
            Key.K      => LibretroInput.ANALOG_RIGHT_DOWN,
            Key.J      => LibretroInput.ANALOG_RIGHT_LEFT,
            Key.L      => LibretroInput.ANALOG_RIGHT_RIGHT,
            _          => uint.MaxValue,
        };

        /// <summary>True for a RetroPad button or a stick direction.</summary>
        public static bool IsTarget(uint id) => id <= LibretroInput.ANALOG_RIGHT_RIGHT;

        /// <summary>Whether WASD drives the left stick on <paramref name="console"/> —
        /// the answer the game window gets from its console handler.</summary>
        public static bool UsesAnalogStick(string console) =>
            ConsoleHandlers.ConsoleHandlerFactory.Create(console).UsesAnalogStick;

        /// <summary>
        /// Every key that plays <paramref name="console"/>, with the target it
        /// presses: the binds first, in their order, then the built-in keys that
        /// are still free. A key bound to two buttons appears twice.
        /// <paramref name="binds"/> are button names from the controller
        /// definition with the key bound to each; names that reach no target
        /// (the Disk Swap chord, hotkeys) are skipped.
        /// </summary>
        public static List<(Key Key, uint Target)> Resolve(
            string console, bool analogStick, IEnumerable<(string ButtonName, Key Key)> binds)
        {
            var result = new List<(Key Key, uint Target)>();
            var boundKeys = new HashSet<Key>();
            var rebound = new HashSet<uint>();
            foreach (var (buttonName, key) in binds)
            {
                if (key == Key.None) continue;
                uint target = LibretroInput.GetButtonId(buttonName, console);
                if (!IsTarget(target)) continue;
                if (!result.Contains((key, target))) result.Add((key, target));
                boundKeys.Add(key);
                rebound.Add(target);
            }
            foreach (var key in BuiltInOrder)
            {
                uint target = BuiltInTarget(key, analogStick);
                if (boundKeys.Contains(key) || rebound.Contains(target)) continue;
                result.Add((key, target));
            }
            return result;
        }

        /// <summary><see cref="Resolve"/> as the game reads it: key → targets.</summary>
        public static Dictionary<Key, uint[]> ToKeyMap(List<(Key Key, uint Target)> resolved)
        {
            var lists = new Dictionary<Key, List<uint>>();
            foreach (var (key, target) in resolved)
            {
                if (!lists.TryGetValue(key, out var list)) lists[key] = list = new List<uint>();
                list.Add(target);
            }
            var map = new Dictionary<Key, uint[]>(lists.Count);
            foreach (var kv in lists) map[kv.Key] = kv.Value.ToArray();
            return map;
        }

        /// <summary>
        /// A saved keyboard list as binds: the identifiers that name a key.
        /// Chords ("A+B", the Disk Swap row) are not button binds and are left out.
        /// </summary>
        public static IEnumerable<(string ButtonName, Key Key)> SavedBinds(
            IEnumerable<Configuration.ButtonMapping> saved)
        {
            foreach (var m in saved)
            {
                string id = m.InputIdentifier ?? "";
                if (id.Length == 0 || id.Contains('+') || char.IsDigit(id[0])) continue;
                if (Enum.TryParse<Key>(id, out var key) && key != Key.None)
                    yield return (m.ButtonName, key);
            }
        }

        /// <summary>
        /// The key itself, not the stand-in WPF reports while Alt is held
        /// (<see cref="Key.System"/>, which is also how F10 arrives) or while an
        /// IME or a dead key is composing.
        /// </summary>
        public static Key RealKey(KeyEventArgs e) => e.Key switch
        {
            Key.System            => e.SystemKey,
            Key.ImeProcessed      => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _                     => e.Key,
        };
    }

    /// <summary>
    /// Player 1's keyboard pad in the game window: which targets are held, and
    /// the stick positions they make. Fed every key-down and key-up; a target
    /// stays pressed while any of its keys is down, and a stick direction pair
    /// held together cancels out.
    /// </summary>
    public sealed class KeyboardPad
    {
        private const short Full = 32767;

        private Dictionary<Key, uint[]> _map = new();
        private readonly HashSet<Key> _held = new();
        private readonly int[] _holds = new int[LibretroInput.ANALOG_RIGHT_RIGHT + 1];

        /// <summary>RetroPad buttons 0-15. Owned by the caller: the emulator
        /// reads the same array for port 0.</summary>
        public bool[] Buttons { get; }

        // libretro convention: up and left negative, down and right positive.
        public short LeftX  { get; private set; }
        public short LeftY  { get; private set; }
        public short RightX { get; private set; }
        public short RightY { get; private set; }

        public KeyboardPad(bool[] buttons) => Buttons = buttons;

        /// <summary>Installs a new key map and lets go of everything held — a
        /// key held across a rebind never saw its release here.</summary>
        public void SetMap(Dictionary<Key, uint[]> map)
        {
            _map = map;
            ReleaseAll();
        }

        public void ReleaseAll()
        {
            _held.Clear();
            Array.Clear(_holds);
            Array.Clear(Buttons);
            LeftX = LeftY = RightX = RightY = 0;
        }

        /// <summary>A key went down or up. Auto-repeat arrives as more downs of
        /// a key already held and changes nothing. Returns whether the key is
        /// one the map uses.</summary>
        public bool Set(Key key, bool pressed)
        {
            if (!_map.TryGetValue(key, out var targets)) return false;
            if (pressed ? !_held.Add(key) : !_held.Remove(key)) return true;
            foreach (uint t in targets)
            {
                if (t >= (uint)_holds.Length) continue;
                _holds[t] = Math.Max(0, _holds[t] + (pressed ? 1 : -1));
                if (t < (uint)Buttons.Length) Buttons[t] = _holds[t] > 0;
            }
            LeftX  = Axis(LibretroInput.ANALOG_LEFT_LEFT,  LibretroInput.ANALOG_LEFT_RIGHT);
            LeftY  = Axis(LibretroInput.ANALOG_LEFT_UP,    LibretroInput.ANALOG_LEFT_DOWN);
            RightX = Axis(LibretroInput.ANALOG_RIGHT_LEFT, LibretroInput.ANALOG_RIGHT_RIGHT);
            RightY = Axis(LibretroInput.ANALOG_RIGHT_UP,   LibretroInput.ANALOG_RIGHT_DOWN);
            return true;
        }

        private short Axis(uint negative, uint positive) =>
            (short)((_holds[positive] > 0 ? Full : 0) - (_holds[negative] > 0 ? Full : 0));
    }
}
