using System;

namespace Emutastic.Services
{
    /// <summary>
    /// Shared libretro input mapping. Translates a button-name + console string
    /// pair into a RetroPad ID (0–15) or an internal analog-deflection ID
    /// (16–23) used by <see cref="ControllerManager"/>'s polling layer.
    ///
    /// Single source of truth — was previously duplicated between
    /// ControllerManager.GetLibretroButtonId and EmulatorWindow.GetLibretroButtonId.
    /// The copies drifted (NeoGeo case bit us once; CDi case was missing from
    /// EmulatorWindow; Vectrex/3DO analog entries diverged). Adding a new
    /// console or button now happens in one place.
    /// </summary>
    public static class LibretroInput
    {
        // Standard libretro RETRO_DEVICE_ID_JOYPAD_* IDs (0–15).
        public const uint JOYPAD_B      = 0;
        public const uint JOYPAD_Y      = 1;
        public const uint JOYPAD_SELECT = 2;
        public const uint JOYPAD_START  = 3;
        public const uint JOYPAD_UP     = 4;
        public const uint JOYPAD_DOWN   = 5;
        public const uint JOYPAD_LEFT   = 6;
        public const uint JOYPAD_RIGHT  = 7;
        public const uint JOYPAD_A      = 8;
        public const uint JOYPAD_X      = 9;
        public const uint JOYPAD_L      = 10;
        public const uint JOYPAD_R      = 11;
        public const uint JOYPAD_L2     = 12;
        public const uint JOYPAD_R2     = 13;
        public const uint JOYPAD_L3     = 14;
        public const uint JOYPAD_R3     = 15;

        // Internal analog-direction IDs (16–23). NOT real libretro IDs — used
        // by ControllerManager.GetButtonState to gate stick deflection above a
        // deadzone. Callers that route to RetroPad input filter these out with
        // a `< 16` check.
        public const uint ANALOG_LEFT_UP     = 16;
        public const uint ANALOG_LEFT_DOWN   = 17;
        public const uint ANALOG_LEFT_LEFT   = 18;
        public const uint ANALOG_LEFT_RIGHT  = 19;
        public const uint ANALOG_RIGHT_UP    = 20;
        public const uint ANALOG_RIGHT_DOWN  = 21;
        public const uint ANALOG_RIGHT_LEFT  = 22;
        public const uint ANALOG_RIGHT_RIGHT = 23;

        /// <summary>
        /// Returns the libretro / internal ID for <paramref name="buttonName"/>
        /// in the context of <paramref name="console"/>, or <c>uint.MaxValue</c>
        /// if the button is unmapped. Returns 16+ for analog deflection IDs;
        /// callers polling RetroPad state must filter with a <c>&lt; 16</c> guard.
        /// </summary>
        public static uint GetButtonId(string buttonName, string console = "")
        {
            string n = buttonName.ToLower();

            switch (console)
            {
                // ── Sega 6-button: A→Y, C→A, Z→R, Mode→Select ───────────────
                case "Genesis":
                case "SegaCD":
                case "Sega32X":
                    return n switch
                    {
                        "a" => JOYPAD_Y, "b" => JOYPAD_B, "c" => JOYPAD_A,
                        "x" => JOYPAD_X, "y" => JOYPAD_L, "z" => JOYPAD_R,
                        "mode" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Sega Saturn (Kronos + Beetle Saturn) ─────────────────────
                // RetroArch's "6-button-on-modern-pad" convention:
                //   JOYPAD_B (0) → A   JOYPAD_A (8) → B   JOYPAD_R (11) → C
                //   JOYPAD_Y (1) → X   JOYPAD_X (9) → Y   JOYPAD_L (10) → Z
                //   JOYPAD_L2 (12) → L-trigger  JOYPAD_R2 (13) → R-trigger
                case "Saturn":
                    return n switch
                    {
                        "a" => JOYPAD_B, "b" => JOYPAD_A, "c" => JOYPAD_R,
                        "x" => JOYPAD_Y, "y" => JOYPAD_X, "z" => JOYPAD_L,
                        "l" => JOYPAD_L2, "r" => JOYPAD_R2,
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── PlayStation / PSP: Sony button names → libretro IDs ─────
                // PSP has only L/R (one pair); PS1 has L1/R1/L2/R2. The
                // ControllerDefinition for PSP exposes the buttons as "L"/"R"
                // and the single analog stick as "Analog Up/Down/Left/Right".
                // Resolver accepts both naming conventions so bindings UI and
                // any hand-edited configs match correctly.
                case "PS1":
                case "PSP":
                    return n switch
                    {
                        "cross" => JOYPAD_B, "circle" => JOYPAD_A,
                        "square" => JOYPAD_Y, "triangle" => JOYPAD_X,
                        "l1" or "l" => JOYPAD_L, "r1" or "r" => JOYPAD_R,
                        "l2" => JOYPAD_L2, "r2" => JOYPAD_R2,
                        "l3" => JOYPAD_L3, "r3" => JOYPAD_R3,
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        "left analog up"    or "analog up"    => ANALOG_LEFT_UP,
                        "left analog down"  or "analog down"  => ANALOG_LEFT_DOWN,
                        "left analog left"  or "analog left"  => ANALOG_LEFT_LEFT,
                        "left analog right" or "analog right" => ANALOG_LEFT_RIGHT,
                        "right analog up"    => ANALOG_RIGHT_UP,
                        "right analog down"  => ANALOG_RIGHT_DOWN,
                        "right analog left"  => ANALOG_RIGHT_LEFT,
                        "right analog right" => ANALOG_RIGHT_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── NEC PC-Engine ────────────────────────────────────────────
                case "TG16":
                case "TGCD":
                    return n switch
                    {
                        "ii" => JOYPAD_B, "i" => JOYPAD_A,
                        "select" => JOYPAD_SELECT, "run" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Nintendo 64 (A=south, B=west; Z=L2; C-buttons via analog) ─
                case "N64":
                    return n switch
                    {
                        "a" => JOYPAD_B, "b" => JOYPAD_Y,
                        "z" => JOYPAD_L2, "l" => JOYPAD_L, "r" => JOYPAD_R,
                        "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        "analog up"    => ANALOG_LEFT_UP,
                        "analog down"  => ANALOG_LEFT_DOWN,
                        "analog left"  => ANALOG_LEFT_LEFT,
                        "analog right" => ANALOG_LEFT_RIGHT,
                        "c up"    => ANALOG_RIGHT_UP,
                        "c down"  => ANALOG_RIGHT_DOWN,
                        "c left"  => ANALOG_RIGHT_LEFT,
                        "c right" => ANALOG_RIGHT_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── GameCube (Dolphin libretro) ──────────────────────────────────
                // GC L/R are analog triggers with a digital click at full travel.
                // Dolphin wires them to L2/R2 (analog via ANALOG_BUTTON index,
                // digital via JOYPAD). GC Z (digital shoulder) goes to L.
                case "GameCube":
                    return n switch
                    {
                        "a" => JOYPAD_A, "b" => JOYPAD_B,
                        "x" => JOYPAD_X, "y" => JOYPAD_Y,
                        "l" => JOYPAD_L2, "r" => JOYPAD_R2, "z" => JOYPAD_L,
                        "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        "analog up"      => ANALOG_LEFT_UP,
                        "analog down"    => ANALOG_LEFT_DOWN,
                        "analog left"    => ANALOG_LEFT_LEFT,
                        "analog right"   => ANALOG_LEFT_RIGHT,
                        "c-stick up"     => ANALOG_RIGHT_UP,
                        "c-stick down"   => ANALOG_RIGHT_DOWN,
                        "c-stick left"   => ANALOG_RIGHT_LEFT,
                        "c-stick right"  => ANALOG_RIGHT_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Nintendo 3DS (Azahar) ────────────────────────────────────
                case "3DS":
                    return n switch
                    {
                        "a" => JOYPAD_A, "b" => JOYPAD_B,
                        "x" => JOYPAD_X, "y" => JOYPAD_Y,
                        "l" => JOYPAD_L, "r" => JOYPAD_R,
                        "zl" => JOYPAD_L2, "zr" => JOYPAD_R2,
                        "home" => JOYPAD_L3, "touch" => JOYPAD_R3,
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        "circle pad up"    => ANALOG_LEFT_UP,
                        "circle pad down"  => ANALOG_LEFT_DOWN,
                        "circle pad left"  => ANALOG_LEFT_LEFT,
                        "circle pad right" => ANALOG_LEFT_RIGHT,
                        "c-stick up"       => ANALOG_RIGHT_UP,
                        "c-stick down"     => ANALOG_RIGHT_DOWN,
                        "c-stick left"     => ANALOG_RIGHT_LEFT,
                        "c-stick right"    => ANALOG_RIGHT_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Sega 8-bit: numbered buttons ─────────────────────────────
                case "SMS":
                case "GameGear":
                case "SG1000":
                    return n switch
                    {
                        "1" => JOYPAD_B, "2" => JOYPAD_A,
                        "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Atari ────────────────────────────────────────────────────
                case "Atari2600":
                    return n switch
                    {
                        "fire" => JOYPAD_B,
                        "select" => JOYPAD_SELECT, "reset" => JOYPAD_START,
                        "left diff a" => JOYPAD_L, "left diff b" => JOYPAD_L2,
                        "right diff a" => JOYPAD_R, "right diff b" => JOYPAD_R2,
                        "color" => JOYPAD_L3, "b/w" => JOYPAD_R3,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                case "Atari7800":
                    return n switch
                    {
                        "fire 1" => JOYPAD_B, "fire 2" => JOYPAD_A,
                        "select" => JOYPAD_SELECT, "pause" => JOYPAD_START,
                        "reset" => JOYPAD_X,
                        "left diff" => JOYPAD_L, "right diff" => JOYPAD_R,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                case "Jaguar":
                    return n switch
                    {
                        "a" => JOYPAD_B, "b" => JOYPAD_A, "c" => JOYPAD_R,
                        "option" => JOYPAD_SELECT, "pause" => JOYPAD_START,
                        "*" => JOYPAD_L, "#" => JOYPAD_Y, "0" => JOYPAD_X,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Sega Dreamcast ───────────────────────────────────────────
                case "Dreamcast":
                    return n switch
                    {
                        "a" => JOYPAD_B, "b" => JOYPAD_A,
                        "x" => JOYPAD_Y, "y" => JOYPAD_X,
                        "start" => JOYPAD_START,
                        "l trigger" => JOYPAD_L2, "r trigger" => JOYPAD_R2,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── ColecoVision ─────────────────────────────────────────────
                case "ColecoVision":
                    return n switch
                    {
                        "left fire" => JOYPAD_B, "right fire" => JOYPAD_A,
                        "1" => JOYPAD_Y, "2" => JOYPAD_X,
                        "3" => JOYPAD_L, "4" => JOYPAD_R,
                        "5" => JOYPAD_L2, "6" => JOYPAD_R2,
                        "*" => JOYPAD_START, "#" => JOYPAD_SELECT,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Vectrex (digital-only; never had analog hardware) ───────
                // See feedback_vectrex_no_analog memory.
                case "Vectrex":
                    return n switch
                    {
                        "1" => JOYPAD_A, "2" => JOYPAD_B,
                        "3" => JOYPAD_X, "4" => JOYPAD_Y,
                        _ => uint.MaxValue
                    };

                // ── 3DO (digital-only; never shipped with an analog pad) ─────
                // See feedback_3do_no_analog memory.
                case "3DO":
                    return n switch
                    {
                        "c" => JOYPAD_A, "b" => JOYPAD_B,
                        "a" => JOYPAD_Y, "x" => JOYPAD_X,
                        "l" => JOYPAD_L, "r" => JOYPAD_R, "p" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Philips CD-i (action buttons + d-pad only) ───────────────
                // Analog support intentionally dropped — see
                // project_cdi_analog_research memory; the CD-i libretro core
                // thresholds the analog stick internally and would need a fork
                // to fix. Without analog entries here, "analog up/down/left/right"
                // bindings simply return uint.MaxValue.
                case "CDi":
                    return n switch
                    {
                        "1" => JOYPAD_B, "2" => JOYPAD_Y, "3" => JOYPAD_A,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Arcade / FBNeo / MAME 2003-Plus (Classic mode numbering) ─
                case "Arcade":
                    return n switch
                    {
                        "button 1" => JOYPAD_Y, "button 2" => JOYPAD_B,
                        "button 3" => JOYPAD_X, "button 4" => JOYPAD_A,
                        "button 5" => JOYPAD_L, "button 6" => JOYPAD_R,
                        "button 7" => JOYPAD_L2, "button 8" => JOYPAD_R2,
                        "coin" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── SNK Neo Geo Pocket ───────────────────────────────────────
                case "NGP":
                    return n switch
                    {
                        "a" => JOYPAD_A, "b" => JOYPAD_B,
                        "option" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };

                // ── Nintendo Virtual Boy (dual d-pad layout) ─────────────────
                case "VirtualBoy":
                    return n switch
                    {
                        "left up"    => JOYPAD_UP,
                        "left down"  => JOYPAD_DOWN,
                        "left left"  => JOYPAD_LEFT,
                        "left right" => JOYPAD_RIGHT,
                        "right up"    => JOYPAD_X,
                        "right down"  => JOYPAD_B,
                        "right left"  => JOYPAD_Y,
                        "right right" => JOYPAD_A,
                        "a" => JOYPAD_A, "b" => JOYPAD_B,
                        "l" => JOYPAD_L, "r" => JOYPAD_R,
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        _ => uint.MaxValue
                    };

                // ── SNK Neo Geo / Geolith ────────────────────────────────────
                // RetroArch convention: A→B, B→A, C→Y, D→X. Without this case
                // the fallback has no entry for "c" or "d" — those button
                // presses get silently dropped (this exact bug bit us once).
                case "NeoGeo":
                case "NeoCD":
                    return n switch
                    {
                        "a" => JOYPAD_B, "b" => JOYPAD_A,
                        "c" => JOYPAD_Y, "d" => JOYPAD_X,
                        "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                        "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                        "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                        _ => uint.MaxValue
                    };
            }

            // ── Nintendo DS (DeSmuME) ────────────────────────────────────────
            // "Touch" is the stylus press for the right-stick emulated pointer;
            // DeSmuME reads it on the JOYPAD_R2 wire ("Tap Stylus" in its input
            // descriptors). Every other DS button uses the standard fallback
            // below, so this case deliberately falls through on no-match.
            if (console == "NDS" && n == "touch")
                return JOYPAD_R2;

            // ── Standard libretro joypad fallback ────────────────────────────
            // NES, SNES, GB/GBC/GBA, NDS, FDS, MSX, etc.
            return n switch
            {
                "b" => JOYPAD_B, "y" => JOYPAD_Y,
                "select" => JOYPAD_SELECT, "start" => JOYPAD_START,
                "up" => JOYPAD_UP, "down" => JOYPAD_DOWN,
                "left" => JOYPAD_LEFT, "right" => JOYPAD_RIGHT,
                "a" => JOYPAD_A, "x" => JOYPAD_X,
                "l" => JOYPAD_L, "r" => JOYPAD_R,
                "l2" => JOYPAD_L2, "r2" => JOYPAD_R2,
                "l3" => JOYPAD_L3, "r3" => JOYPAD_R3,
                "analog up"    => ANALOG_LEFT_UP,
                "analog down"  => ANALOG_LEFT_DOWN,
                "analog left"  => ANALOG_LEFT_LEFT,
                "analog right" => ANALOG_LEFT_RIGHT,
                _ => uint.MaxValue
            };
        }
    }
}
