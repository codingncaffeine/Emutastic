using System.Collections.Generic;
using System.Windows.Input;

namespace Emutastic.Services
{
    /// <summary>
    /// Maps WPF Key values to libretro RETROK_* codes and tracks per-key press state.
    /// Used by cores that poll RETRO_DEVICE_KEYBOARD (e.g. DOSBox Pure).
    /// </summary>
    public static class RetroKeyboardMap
    {
        // Subset of retro_key (libretro.h). Values matter — they're IDs the core queries.
        public const uint RETROK_BACKSPACE = 8;
        public const uint RETROK_TAB       = 9;
        public const uint RETROK_RETURN    = 13;
        public const uint RETROK_PAUSE     = 19;
        public const uint RETROK_ESCAPE    = 27;
        public const uint RETROK_SPACE     = 32;
        public const uint RETROK_QUOTE     = 39;
        public const uint RETROK_COMMA     = 44;
        public const uint RETROK_MINUS     = 45;
        public const uint RETROK_PERIOD    = 46;
        public const uint RETROK_SLASH     = 47;
        public const uint RETROK_0         = 48;
        public const uint RETROK_9         = 57;
        public const uint RETROK_SEMICOLON = 59;
        public const uint RETROK_EQUALS    = 61;
        public const uint RETROK_LEFTBRACKET  = 91;
        public const uint RETROK_BACKSLASH    = 92;
        public const uint RETROK_RIGHTBRACKET = 93;
        public const uint RETROK_BACKQUOTE    = 96;
        public const uint RETROK_a         = 97;
        public const uint RETROK_z         = 122;
        public const uint RETROK_DELETE    = 127;

        public const uint RETROK_KP0       = 256;
        public const uint RETROK_KP9       = 265;
        public const uint RETROK_KP_PERIOD = 266;
        public const uint RETROK_KP_DIVIDE = 267;
        public const uint RETROK_KP_MULTIPLY = 268;
        public const uint RETROK_KP_MINUS  = 269;
        public const uint RETROK_KP_PLUS   = 270;
        public const uint RETROK_KP_ENTER  = 271;

        public const uint RETROK_UP        = 273;
        public const uint RETROK_DOWN      = 274;
        public const uint RETROK_RIGHT     = 275;
        public const uint RETROK_LEFT      = 276;
        public const uint RETROK_INSERT    = 277;
        public const uint RETROK_HOME      = 278;
        public const uint RETROK_END       = 279;
        public const uint RETROK_PAGEUP    = 280;
        public const uint RETROK_PAGEDOWN  = 281;

        public const uint RETROK_F1        = 282;
        public const uint RETROK_F15       = 296;

        public const uint RETROK_NUMLOCK   = 300;
        public const uint RETROK_CAPSLOCK  = 301;
        public const uint RETROK_SCROLLOCK = 302;
        public const uint RETROK_RSHIFT    = 303;
        public const uint RETROK_LSHIFT    = 304;
        public const uint RETROK_RCTRL     = 305;
        public const uint RETROK_LCTRL     = 306;
        public const uint RETROK_RALT      = 307;
        public const uint RETROK_LALT      = 308;
        public const uint RETROK_LSUPER    = 311;
        public const uint RETROK_RSUPER    = 312;
        public const uint RETROK_PRINT     = 316;
        public const uint RETROK_MENU      = 319;

        public const uint RETROK_LAST      = 324;

        private static readonly Dictionary<Key, uint> Map = BuildMap();

        private static Dictionary<Key, uint> BuildMap()
        {
            var m = new Dictionary<Key, uint>
            {
                { Key.Back,          RETROK_BACKSPACE },
                { Key.Tab,           RETROK_TAB       },
                { Key.Enter,         RETROK_RETURN    },
                { Key.Pause,         RETROK_PAUSE     },
                { Key.Escape,        RETROK_ESCAPE    },
                { Key.Space,         RETROK_SPACE     },
                { Key.OemQuotes,     RETROK_QUOTE     },
                { Key.OemComma,      RETROK_COMMA     },
                { Key.OemMinus,      RETROK_MINUS     },
                { Key.OemPeriod,     RETROK_PERIOD    },
                { Key.OemQuestion,   RETROK_SLASH     },
                { Key.OemSemicolon,  RETROK_SEMICOLON },
                { Key.OemPlus,       RETROK_EQUALS    },
                { Key.OemOpenBrackets,  RETROK_LEFTBRACKET  },
                { Key.OemPipe,       RETROK_BACKSLASH },
                { Key.OemCloseBrackets, RETROK_RIGHTBRACKET },
                { Key.OemTilde,      RETROK_BACKQUOTE },
                { Key.Delete,        RETROK_DELETE    },

                { Key.Up,            RETROK_UP        },
                { Key.Down,          RETROK_DOWN      },
                { Key.Right,         RETROK_RIGHT     },
                { Key.Left,          RETROK_LEFT      },
                { Key.Insert,        RETROK_INSERT    },
                { Key.Home,          RETROK_HOME      },
                { Key.End,           RETROK_END       },
                { Key.PageUp,        RETROK_PAGEUP    },
                { Key.PageDown,      RETROK_PAGEDOWN  },

                { Key.NumLock,       RETROK_NUMLOCK   },
                { Key.CapsLock,      RETROK_CAPSLOCK  },
                { Key.Scroll,        RETROK_SCROLLOCK },
                { Key.LeftShift,     RETROK_LSHIFT    },
                { Key.RightShift,    RETROK_RSHIFT    },
                { Key.LeftCtrl,      RETROK_LCTRL     },
                { Key.RightCtrl,     RETROK_RCTRL     },
                { Key.LeftAlt,       RETROK_LALT      },
                { Key.RightAlt,      RETROK_RALT      },
                { Key.LWin,          RETROK_LSUPER    },
                { Key.RWin,          RETROK_RSUPER    },
                { Key.PrintScreen,   RETROK_PRINT     },
                { Key.Apps,          RETROK_MENU      },

                // Keypad
                { Key.NumPad0,       RETROK_KP0       },
                { Key.NumPad1,       RETROK_KP0 + 1   },
                { Key.NumPad2,       RETROK_KP0 + 2   },
                { Key.NumPad3,       RETROK_KP0 + 3   },
                { Key.NumPad4,       RETROK_KP0 + 4   },
                { Key.NumPad5,       RETROK_KP0 + 5   },
                { Key.NumPad6,       RETROK_KP0 + 6   },
                { Key.NumPad7,       RETROK_KP0 + 7   },
                { Key.NumPad8,       RETROK_KP0 + 8   },
                { Key.NumPad9,       RETROK_KP9       },
                { Key.Decimal,       RETROK_KP_PERIOD },
                { Key.Divide,        RETROK_KP_DIVIDE },
                { Key.Multiply,      RETROK_KP_MULTIPLY },
                { Key.Subtract,      RETROK_KP_MINUS  },
                { Key.Add,           RETROK_KP_PLUS   },
            };

            // Digits: WPF Key.D0..D9 → RETROK_0..9
            for (int i = 0; i <= 9; i++)
                m[Key.D0 + i] = RETROK_0 + (uint)i;

            // Letters: WPF Key.A..Z → RETROK_a..z (libretro uses lowercase ASCII)
            for (int i = 0; i < 26; i++)
                m[Key.A + i] = RETROK_a + (uint)i;

            // Function keys: F1..F15
            for (int i = 0; i < 15; i++)
                m[Key.F1 + i] = RETROK_F1 + (uint)i;

            return m;
        }

        /// <summary>Returns the RETROK_* value for the given WPF Key, or 0 if unmapped.</summary>
        public static uint ToRetroKey(Key key)
            => Map.TryGetValue(key, out uint v) ? v : 0;
    }

    /// <summary>Thread-safe press state for RETROK_* codes. Size fixed at RETROK_LAST.</summary>
    public class RetroKeyboardState
    {
        private readonly bool[] _state = new bool[RetroKeyboardMap.RETROK_LAST];

        public void SetKey(Key key, bool pressed)
        {
            uint r = RetroKeyboardMap.ToRetroKey(key);
            if (r == 0 || r >= _state.Length) return;
            _state[r] = pressed;
        }

        public bool IsPressed(uint retroKey)
            => retroKey < _state.Length && _state[retroKey];

        public void Clear()
        {
            for (int i = 0; i < _state.Length; i++) _state[i] = false;
        }
    }
}
