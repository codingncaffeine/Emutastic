using System.Collections.Generic;

namespace Emutastic.Configuration
{
    public static class ControllerDefinitions
    {
        // Group name constants
        private const string GDPad     = "D-Pad";
        private const string GFace     = "Face Buttons";
        private const string GShoulder = "Shoulder Buttons";
        private const string GTrigger  = "Triggers";
        private const string GSystem   = "System Buttons";
        private const string GLAnalog  = "Left Analog";
        private const string GRAnalog  = "Right Analog";
        private const string GCStick   = "C-Stick";
        private const string GAnalog   = "Analog Stick";

        public static readonly Dictionary<string, ControllerDefinition> AllControllers = new()
        {
            // ── Nintendo ──────────────────────────────────────────────────────
            ["NES"] = new ControllerDefinition
            {
                Name = "Nintendo Entertainment System",
                ControllerImage = "/Assets/images/NES/controller_nes@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  80, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",   "Down",   140, 160, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",   "Left",   100, 120, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",  "Right",  180, 120, ButtonType.DPad,   60, 60, GDPad),
                    new("B",      "B",      400, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      450, 120, ButtonType.Button, 35, 35, GFace),
                    new("Select", "Select", 240, 200, ButtonType.Button, 40, 20, GSystem),
                    new("Start",  "Start",  320, 200, ButtonType.Button, 40, 20, GSystem),
                }
            },
            ["FDS"] = new ControllerDefinition
            {
                Name = "Famicom Disk System",
                ControllerImage = "/Assets/images/famicom/famicom.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  80, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",   "Down",   140, 160, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",   "Left",   100, 120, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",  "Right",  180, 120, ButtonType.DPad,   60, 60, GDPad),
                    new("B",      "B",      400, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      450, 120, ButtonType.Button, 35, 35, GFace),
                    new("Select", "Select", 240, 200, ButtonType.Button, 40, 20, GSystem),
                    new("Start",  "Start",  320, 200, ButtonType.Button, 40, 20, GSystem),
                }
            },
            ["SNES"] = new ControllerDefinition
            {
                Name = "Super Nintendo",
                ControllerImage = "/Assets/images/SNES/controller_snes_usa@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Y",      "Y",      340, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",      "B",      380, 170, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      420, 130, ButtonType.Button, 35, 35, GFace),
                    new("X",      "X",      380,  90, ButtonType.Button, 35, 35, GFace),
                    new("L",      "L",       60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",      "R",      430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Start",  "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["N64"] = new ControllerDefinition
            {
                Name = "Nintendo 64",
                ControllerImage = "/Assets/images/N64/controller_n64@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",           "Up",           140,  70, ButtonType.DPad,          70, 70, GDPad),
                    new("Down",         "Down",         140, 150, ButtonType.DPad,          70, 70, GDPad),
                    new("Left",         "Left",         100, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("Right",        "Right",        180, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("B",            "B",            340, 140, ButtonType.Button,        35, 35, GFace),
                    new("A",            "A",            380, 100, ButtonType.Button,        35, 35, GFace),
                    new("C Up",         "CUp",          300,  80, ButtonType.Button,        30, 30, GFace),
                    new("C Down",       "CDown",        300, 140, ButtonType.Button,        30, 30, GFace),
                    new("C Left",       "CLeft",        270, 110, ButtonType.Button,        30, 30, GFace),
                    new("C Right",      "CRight",       330, 110, ButtonType.Button,        30, 30, GFace),
                    new("L",            "L",             80,  60, ButtonType.Button,        80, 25, GShoulder),
                    new("R",            "R",            440,  60, ButtonType.Button,        80, 25, GShoulder),
                    new("Z",            "Z",            200,  60, ButtonType.Button,        40, 20, GTrigger),
                    new("Start",        "Start",        320, 190, ButtonType.Button,        50, 20, GSystem),
                    new("Analog Up",    "AnalogUp",     140,  90, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Down",  "AnalogDown",   140, 130, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Left",  "AnalogLeft",   120, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Right", "AnalogRight",  160, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                }
            },
            ["GameCube"] = new ControllerDefinition
            {
                Name = "Nintendo GameCube",
                ControllerImage = "/Assets/images/Gamecube/controller_gamecube@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",            "Up",            140,  70, ButtonType.DPad,          70, 70, GDPad),
                    new("Down",          "Down",          140, 150, ButtonType.DPad,          70, 70, GDPad),
                    new("Left",          "Left",          100, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("Right",         "Right",         180, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("A",             "A",             380, 100, ButtonType.Button,        35, 35, GFace),
                    new("B",             "B",             340, 140, ButtonType.Button,        35, 35, GFace),
                    new("X",             "X",             420, 120, ButtonType.Button,        35, 35, GFace),
                    new("Y",             "Y",             380, 160, ButtonType.Button,        35, 35, GFace),
                    new("L",             "L",              80,  60, ButtonType.Button,        80, 25, GShoulder),
                    new("R",             "R",             440,  60, ButtonType.Button,        80, 25, GShoulder),
                    new("Z",             "Z",             200,  60, ButtonType.Button,        40, 20, GTrigger),
                    new("Start",         "Start",         320, 190, ButtonType.Button,        50, 20, GSystem),
                    new("Analog Up",     "AnalogUp",      140,  90, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Analog Down",   "AnalogDown",    140, 130, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Analog Left",   "AnalogLeft",    120, 110, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Analog Right",  "AnalogRight",   160, 110, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("C-Stick Up",    "CStickUp",      400, 120, ButtonType.AnalogDirection, 30, 30, GCStick),
                    new("C-Stick Down",  "CStickDown",    400, 160, ButtonType.AnalogDirection, 30, 30, GCStick),
                    new("C-Stick Left",  "CStickLeft",    380, 140, ButtonType.AnalogDirection, 30, 30, GCStick),
                    new("C-Stick Right", "CStickRight",   420, 140, ButtonType.AnalogDirection, 30, 30, GCStick),
                }
            },
            ["GB"] = new ControllerDefinition
            {
                Name = "Game Boy",
                ControllerImage = "/Assets/images/Game Boy/controller_gb@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("B",      "B",      340, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Start",  "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["GBC"] = new ControllerDefinition
            {
                Name = "Game Boy Color",
                ControllerImage = "/Assets/images/Game Boy/controller_gb@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("B",      "B",      340, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Start",  "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["GBA"] = new ControllerDefinition
            {
                Name = "Game Boy Advance",
                ControllerImage = "/Assets/images/Game Boy Advance/controller_gba@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("B",      "B",      340, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("L",      "L",       60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",      "R",      430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Start",  "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["NDS"] = new ControllerDefinition
            {
                Name = "Nintendo DS",
                ControllerImage = "/Assets/images/NDS/controller_nds@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Y",      "Y",      380, 160, ButtonType.Button, 35, 35, GFace),
                    new("B",      "B",      340, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("X",      "X",      420,  90, ButtonType.Button, 35, 35, GFace),
                    new("L",      "L",       60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",      "R",      430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Start",  "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["3DS"] = new ControllerDefinition
            {
                Name = "Nintendo 3DS",
                ControllerImage = "/Assets/images/3DS/3ds.jpg",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     120, 200, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",   "Down",   120, 280, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",   "Left",    80, 240, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",  "Right",  160, 240, ButtonType.DPad,   60, 60, GDPad),
                    new("Circle Pad Up",    "AnalogUp",    170, 140, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Circle Pad Down",  "AnalogDown",  170, 180, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Circle Pad Left",  "AnalogLeft",  150, 160, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Circle Pad Right", "AnalogRight", 190, 160, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("C-Stick Up",    "CStickUp",    430, 130, ButtonType.AnalogDirection, 25, 25, GCStick),
                    new("C-Stick Down",  "CStickDown",  430, 165, ButtonType.AnalogDirection, 25, 25, GCStick),
                    new("C-Stick Left",  "CStickLeft",  412, 148, ButtonType.AnalogDirection, 25, 25, GCStick),
                    new("C-Stick Right", "CStickRight", 448, 148, ButtonType.AnalogDirection, 25, 25, GCStick),
                    new("B",      "B",      420, 210, ButtonType.Button, 30, 30, GFace),
                    new("A",      "A",      450, 180, ButtonType.Button, 30, 30, GFace),
                    new("Y",      "Y",      390, 180, ButtonType.Button, 30, 30, GFace),
                    new("X",      "X",      420, 150, ButtonType.Button, 30, 30, GFace),
                    new("L",      "L",       60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",      "R",      430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("ZL",     "ZL",      60,  10, ButtonType.Button, 70, 20, GShoulder),
                    new("ZR",     "ZR",     430,  10, ButtonType.Button, 70, 20, GShoulder),
                    new("Start",  "Start",  380, 250, ButtonType.Button, 50, 20, GSystem),
                    new("Select", "Select", 380, 275, ButtonType.Button, 50, 20, GSystem),
                    new("Home",   "Home",   380, 300, ButtonType.Button, 50, 20, GSystem),
                }
            },
            ["VirtualBoy"] = new ControllerDefinition
            {
                Name = "Virtual Boy",
                ControllerImage = "/Assets/images/VB/controller_vb@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Left Up",    "LeftUp",    100,  70, ButtonType.DPad,   60, 60, "Left D-Pad"),
                    new("Left Down",  "LeftDown",  100, 150, ButtonType.DPad,   60, 60, "Left D-Pad"),
                    new("Left Left",  "LeftLeft",   70, 110, ButtonType.DPad,   60, 60, "Left D-Pad"),
                    new("Left Right", "LeftRight", 130, 110, ButtonType.DPad,   60, 60, "Left D-Pad"),
                    new("Right Up",   "RightUp",   400,  70, ButtonType.DPad,   60, 60, "Right D-Pad"),
                    new("Right Down", "RightDown", 400, 150, ButtonType.DPad,   60, 60, "Right D-Pad"),
                    new("Right Left", "RightLeft", 370, 110, ButtonType.DPad,   60, 60, "Right D-Pad"),
                    new("Right Right","RightRight",430, 110, ButtonType.DPad,   60, 60, "Right D-Pad"),
                    new("B",          "B",         340, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",          "A",         380, 120, ButtonType.Button, 35, 35, GFace),
                    new("L",          "L",          60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",          "R",         430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("Select",     "Select",    230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Start",      "Start",     285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },

            // ── Sega ──────────────────────────────────────────────────────────
            ["Genesis"] = new ControllerDefinition
            {
                Name = "Sega Genesis",
                ControllerImage = "/Assets/images/Genesis/controller_genesis@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("A",      "A",      340, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",      "B",      380, 110, ButtonType.Button, 35, 35, GFace),
                    new("C",      "C",      420, 130, ButtonType.Button, 35, 35, GFace),
                    new("X",      "X",      340,  90, ButtonType.Button, 35, 35, GFace),
                    new("Y",      "Y",      380,  70, ButtonType.Button, 35, 35, GFace),
                    new("Z",      "Z",      420,  90, ButtonType.Button, 35, 35, GFace),
                    new("Mode",   "Select", 240, 190, ButtonType.Button, 50, 20, GSystem),
                    new("Start",  "Start",  320, 190, ButtonType.Button, 50, 20, GSystem),
                }
            },
            ["SegaCD"] = new ControllerDefinition
            {
                Name = "Sega CD",
                ControllerImage = "/Assets/images/Genesis/controller_genesis@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",    "Up",    140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",  "Down",  140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",  "Left",  100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right", "Right", 180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("A",     "A",     340, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",     "B",     380, 110, ButtonType.Button, 35, 35, GFace),
                    new("C",     "C",     420, 130, ButtonType.Button, 35, 35, GFace),
                    new("X",     "X",     340,  90, ButtonType.Button, 35, 35, GFace),
                    new("Y",     "Y",     380,  70, ButtonType.Button, 35, 35, GFace),
                    new("Z",     "Z",     420,  90, ButtonType.Button, 35, 35, GFace),
                    new("Mode",  "Select",240, 190, ButtonType.Button, 50, 20, GSystem),
                    new("Start", "Start", 320, 190, ButtonType.Button, 50, 20, GSystem),
                }
            },
            ["Sega32X"] = new ControllerDefinition
            {
                Name = "Sega 32X",
                ControllerImage = "/Assets/images/Genesis/controller_genesis@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",    "Up",    140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",  "Down",  140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",  "Left",  100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right", "Right", 180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("A",     "A",     340, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",     "B",     380, 110, ButtonType.Button, 35, 35, GFace),
                    new("C",     "C",     420, 130, ButtonType.Button, 35, 35, GFace),
                    new("X",     "X",     340,  90, ButtonType.Button, 35, 35, GFace),
                    new("Y",     "Y",     380,  70, ButtonType.Button, 35, 35, GFace),
                    new("Z",     "Z",     420,  90, ButtonType.Button, 35, 35, GFace),
                    new("Mode",  "Select",240, 190, ButtonType.Button, 50, 20, GSystem),
                    new("Start", "Start", 320, 190, ButtonType.Button, 50, 20, GSystem),
                }
            },
            ["Saturn"] = new ControllerDefinition
            {
                Name = "Sega Saturn",
                ControllerImage = "/Assets/images/Sega Saturn/controller_sega_saturn@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("A",      "A",      420, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",      "B",      380, 170, ButtonType.Button, 35, 35, GFace),
                    new("C",      "C",      460,  90, ButtonType.Button, 35, 35, GFace),
                    new("X",      "X",      380,  90, ButtonType.Button, 35, 35, GFace),
                    new("Y",      "Y",      340, 130, ButtonType.Button, 35, 35, GFace),
                    new("Z",      "Z",      420,  90, ButtonType.Button, 35, 35, GFace),
                    new("L",      "L",       60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",      "R",      430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Start",  "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["SMS"] = new ControllerDefinition
            {
                Name = "Sega Master System",
                ControllerImage = "/Assets/images/SMS/controller_sms@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",    "Up",    140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",  "Down",  140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",  "Left",  100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right", "Right", 180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("1",     "Button1",380, 120, ButtonType.Button, 35, 35, GFace),
                    new("2",     "Button2",340, 140, ButtonType.Button, 35, 35, GFace),
                    new("Start", "Start", 285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["GameGear"] = new ControllerDefinition
            {
                Name = "Game Gear",
                ControllerImage = "/Assets/images/Game Gear/controller_gamegear@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",    "Up",    140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",  "Down",  140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",  "Left",  100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right", "Right", 180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("1",     "Button1",380, 120, ButtonType.Button, 35, 35, GFace),
                    new("2",     "Button2",340, 140, ButtonType.Button, 35, 35, GFace),
                    new("Start", "Start", 285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["SG1000"] = new ControllerDefinition
            {
                Name = "Sega SG-1000",
                ControllerImage = "/Assets/images/SG1000/controller_sg1000@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",    "Up",    140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",  "Down",  140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",  "Left",  100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right", "Right", 180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("1",     "Button1",380, 120, ButtonType.Button, 35, 35, GFace),
                    new("2",     "Button2",340, 140, ButtonType.Button, 35, 35, GFace),
                }
            },

            // ── Sony ──────────────────────────────────────────────────────────
            ["PS1"] = new ControllerDefinition
            {
                Name = "PlayStation",
                ControllerImage = "/Assets/images/PlayStation/controller_psx@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",                 "Up",               140,  70, ButtonType.DPad,          70, 70, GDPad),
                    new("Down",               "Down",             140, 150, ButtonType.DPad,          70, 70, GDPad),
                    new("Left",               "Left",             100, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("Right",              "Right",            180, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("Cross",              "Cross",            380, 170, ButtonType.Button,        35, 35, GFace),
                    new("Circle",             "Circle",           420, 130, ButtonType.Button,        35, 35, GFace),
                    new("Square",             "Square",           340, 130, ButtonType.Button,        35, 35, GFace),
                    new("Triangle",           "Triangle",         380,  90, ButtonType.Button,        35, 35, GFace),
                    new("L1",                 "L1",                60,  30, ButtonType.Button,        70, 25, GShoulder),
                    new("R1",                 "R1",               430,  30, ButtonType.Button,        70, 25, GShoulder),
                    new("L2",                 "L2",                60,  60, ButtonType.Button,        70, 25, GTrigger),
                    new("R2",                 "R2",               430,  60, ButtonType.Button,        70, 25, GTrigger),
                    new("Select",             "Select",           230, 190, ButtonType.Button,        45, 20, GSystem),
                    new("Start",              "Start",            285, 190, ButtonType.Button,        45, 20, GSystem),
                    new("Left Analog Up",     "LeftAnalogUp",     140,  50, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Down",   "LeftAnalogDown",   140,  90, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Left",   "LeftAnalogLeft",   120,  70, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Right",  "LeftAnalogRight",  160,  70, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Right Analog Up",    "RightAnalogUp",    400, 100, ButtonType.AnalogDirection, 30, 30, GRAnalog),
                    new("Right Analog Down",  "RightAnalogDown",  400, 140, ButtonType.AnalogDirection, 30, 30, GRAnalog),
                    new("Right Analog Left",  "RightAnalogLeft",  380, 120, ButtonType.AnalogDirection, 30, 30, GRAnalog),
                    new("Right Analog Right", "RightAnalogRight", 420, 120, ButtonType.AnalogDirection, 30, 30, GRAnalog),
                }
            },
            ["PSP"] = new ControllerDefinition
            {
                Name = "PlayStation Portable",
                ControllerImage = "/Assets/images/PSP/controller_psp@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",        "Up",        140,  70, ButtonType.DPad,          70, 70, GDPad),
                    new("Down",      "Down",       140, 150, ButtonType.DPad,          70, 70, GDPad),
                    new("Left",      "Left",       100, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("Right",     "Right",      180, 110, ButtonType.DPad,          70, 70, GDPad),
                    new("Cross",     "Cross",      380, 170, ButtonType.Button,        35, 35, GFace),
                    new("Circle",    "Circle",     420, 130, ButtonType.Button,        35, 35, GFace),
                    new("Square",    "Square",     340, 130, ButtonType.Button,        35, 35, GFace),
                    new("Triangle",  "Triangle",   380,  90, ButtonType.Button,        35, 35, GFace),
                    new("L",         "L",           60,  30, ButtonType.Button,        70, 25, GShoulder),
                    new("R",         "R",          430,  30, ButtonType.Button,        70, 25, GShoulder),
                    new("Select",    "Select",     230, 190, ButtonType.Button,        45, 20, GSystem),
                    new("Start",     "Start",      285, 190, ButtonType.Button,        45, 20, GSystem),
                    new("Home",      "Home",       260, 190, ButtonType.Button,        30, 20, GSystem),
                    new("Analog Up",    "AnalogUp",    140,  90, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Down",  "AnalogDown",  140, 130, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Left",  "AnalogLeft",  120, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Right", "AnalogRight", 160, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                }
            },

            // ── NEC ───────────────────────────────────────────────────────────
            ["TG16"] = new ControllerDefinition
            {
                Name = "TurboGrafx-16",
                ControllerImage = "/Assets/images/TG16/controller_tg16@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("II",     "II",     340, 140, ButtonType.Button, 35, 35, GFace),
                    new("I",      "I",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Run",    "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["TGCD"] = new ControllerDefinition
            {
                Name = "TurboGrafx-CD",
                ControllerImage = "/Assets/images/TG16/controller_tg16@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("II",     "II",     340, 140, ButtonType.Button, 35, 35, GFace),
                    new("I",      "I",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Select", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Run",    "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            // ── SNK ───────────────────────────────────────────────────────────
            ["NGP"] = new ControllerDefinition
            {
                Name = "Neo Geo Pocket",
                ControllerImage = "/Assets/images/NGP/controller_ngp@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("B",      "B",      340, 140, ButtonType.Button, 35, 35, GFace),
                    new("A",      "A",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Option", "Option", 260, 190, ButtonType.Button, 50, 20, GSystem),
                }
            },

            ["NeoGeo"] = new ControllerDefinition
            {
                Name = "Neo Geo",
                ControllerImage = "/Assets/images/neogeo/neogeo_arcade_control_panel.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("A",      "A",      320, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",      "B",      360, 110, ButtonType.Button, 35, 35, GFace),
                    new("C",      "C",      400, 100, ButtonType.Button, 35, 35, GFace),
                    new("D",      "D",      440,  95, ButtonType.Button, 35, 35, GFace),
                    new("Start",  "Start",  260, 190, ButtonType.Button, 50, 20, GSystem),
                    new("Select", "Select", 200, 190, ButtonType.Button, 50, 20, GSystem),
                }
            },

            // ── Atari ─────────────────────────────────────────────────────────
            ["Atari2600"] = new ControllerDefinition
            {
                Name = "Atari 2600",
                ControllerImage = "/Assets/images/Atari 2600/controller_2600@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",         "Up",     140,  70, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",       "Down",   140, 150, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",       "Left",   100, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",      "Right",  180, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Fire",       "B",      380, 140, ButtonType.Button, 35, 35, GFace),
                    // Console switches (Stella: SELECT=Select, START=Reset)
                    new("Select",     "Select", 240, 200, ButtonType.Button, 50, 20, GSystem),
                    new("Reset",      "Start",  320, 200, ButtonType.Button, 50, 20, GSystem),
                    // Difficulty switches — L/L2 = Left Diff A/B, R/R2 = Right Diff A/B
                    new("Left Diff A",  "L",    240, 240, ButtonType.Button, 40, 20, "Switch"),
                    new("Left Diff B",  "L2",   290, 240, ButtonType.Button, 40, 20, "Switch"),
                    new("Right Diff A", "R",    240, 270, ButtonType.Button, 40, 20, "Switch"),
                    new("Right Diff B", "R2",   290, 270, ButtonType.Button, 40, 20, "Switch"),
                    // TV type switches — L3 = Color, R3 = B/W
                    new("Color",        "L3",   240, 300, ButtonType.Button, 40, 20, "Switch"),
                    new("B/W",          "R3",   290, 300, ButtonType.Button, 40, 20, "Switch"),
                }
            },

            ["Atari7800"] = new ControllerDefinition
            {
                Name = "Atari 7800",
                ControllerImage = "/Assets/images/Atari 7800/controller_7800@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",         "Up",     140,  70, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",       "Down",   140, 150, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",       "Left",   100, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",      "Right",  180, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Fire 1",     "B",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Fire 2",     "A",      340, 140, ButtonType.Button, 35, 35, GFace),
                    // Console switches (ProSystem: SELECT=Select, START=Pause, X=Reset)
                    new("Select",     "Select", 230, 200, ButtonType.Button, 45, 20, GSystem),
                    new("Pause",      "Start",  300, 200, ButtonType.Button, 45, 20, GSystem),
                    new("Reset",      "X",      370, 200, ButtonType.Button, 45, 20, GSystem),
                    // Difficulty toggles — L = Left Diff, R = Right Diff
                    new("Left Diff",  "L",      250, 240, ButtonType.Button, 40, 20, "Switch"),
                    new("Right Diff", "R",      300, 240, ButtonType.Button, 40, 20, "Switch"),
                }
            },
            ["Jaguar"] = new ControllerDefinition
            {
                Name = "Atari Jaguar",
                ControllerImage = "/Assets/images/Atari Jaguar/controller_atari jaguar@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("A",      "A",      340, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",      "B",      380, 110, ButtonType.Button, 35, 35, GFace),
                    new("C",      "C",      420, 130, ButtonType.Button, 35, 35, GFace),
                    new("Pause",  "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Option", "Select", 230, 190, ButtonType.Button, 45, 20, GSystem),
                    new("*",      "L",      180, 260, ButtonType.Button, 30, 30, "Keypad"),
                    new("#",      "R",      260, 260, ButtonType.Button, 30, 30, "Keypad"),
                    new("0",      "X",      220, 200, ButtonType.Button, 30, 30, "Keypad"),
                    new("1",      "Y",      180, 200, ButtonType.Button, 30, 30, "Keypad"),
                    new("2",      "A2",     220, 200, ButtonType.Button, 30, 30, "Keypad"),
                    new("3",      "B2",     260, 200, ButtonType.Button, 30, 30, "Keypad"),
                }
            },

            // ── Sega ──────────────────────────────────────────────────────────
            ["Dreamcast"] = new ControllerDefinition
            {
                Name = "Sega Dreamcast",
                ControllerImage = "/Assets/images/Dreamcast/dreamcast.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",               "Up",               140,  70, ButtonType.DPad,            70, 70, GDPad),
                    new("Down",             "Down",             140, 150, ButtonType.DPad,            70, 70, GDPad),
                    new("Left",             "Left",             100, 110, ButtonType.DPad,            70, 70, GDPad),
                    new("Right",            "Right",            180, 110, ButtonType.DPad,            70, 70, GDPad),
                    new("A",                "A",                420, 150, ButtonType.Button,          35, 35, GFace),
                    new("B",                "B",                460, 110, ButtonType.Button,          35, 35, GFace),
                    new("X",                "X",                380, 110, ButtonType.Button,          35, 35, GFace),
                    new("Y",                "Y",                420,  70, ButtonType.Button,          35, 35, GFace),
                    new("Start",            "Start",            285, 190, ButtonType.Button,          45, 20, GSystem),
                    new("L Trigger",        "L2",                60,  30, ButtonType.Button,          70, 25, GTrigger),
                    new("R Trigger",        "R2",               430,  30, ButtonType.Button,          70, 25, GTrigger),
                    new("Left Analog Up",   "LeftAnalogUp",     140,  50, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Down", "LeftAnalogDown",   140,  90, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Left", "LeftAnalogLeft",   120,  70, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Right","LeftAnalogRight",  160,  70, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                }
            },

            // ── Others ────────────────────────────────────────────────────────
            ["ColecoVision"] = new ControllerDefinition
            {
                Name = "ColecoVision",
                ControllerImage = "/Assets/images/ColecoVision/controller_colecovision@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",    "Up",    140,  70, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",  "Down",  140, 150, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",  "Left",  100, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Right", "Right", 180, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("L",     "L",      60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",     "R",     430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("1",     "A",     380, 120, ButtonType.Button, 30, 30, "Keypad"),
                    new("2",     "B",     420, 120, ButtonType.Button, 30, 30, "Keypad"),
                    new("3",     "X",     380, 160, ButtonType.Button, 30, 30, "Keypad"),
                    new("4",     "Y",     420, 160, ButtonType.Button, 30, 30, "Keypad"),
                }
            },

            ["Vectrex"] = new ControllerDefinition
            {
                Name = "Vectrex",
                ControllerImage = "/Assets/images/Vectrex/controller_vectrex_eu@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Analog Up",    "AnalogUp",    140,  90, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Down",  "AnalogDown",  140, 130, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Left",  "AnalogLeft",  120, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Right", "AnalogRight", 160, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("1",            "A",           380, 120, ButtonType.Button,          35, 35, GFace),
                    new("2",            "B",           340, 140, ButtonType.Button,          35, 35, GFace),
                    new("3",            "X",           380, 160, ButtonType.Button,          35, 35, GFace),
                    new("4",            "Y",           420, 140, ButtonType.Button,          35, 35, GFace),
                }
            },
            ["CDi"] = new ControllerDefinition
            {
                Name = "Philips CD-i",
                ControllerImage = "/Assets/images/cdi/cdi_controller.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",           "Up",           140,  70, ButtonType.DPad,            70, 70, GDPad),
                    new("Down",         "Down",         140, 150, ButtonType.DPad,            70, 70, GDPad),
                    new("Left",         "Left",         100, 110, ButtonType.DPad,            70, 70, GDPad),
                    new("Right",        "Right",        180, 110, ButtonType.DPad,            70, 70, GDPad),
                    new("1",            "Button 1",     340, 130, ButtonType.Button,          35, 35, GFace),
                    new("2",            "Button 2",     380, 100, ButtonType.Button,          35, 35, GFace),
                    new("3",            "Button 3",     420, 130, ButtonType.Button,          35, 35, GFace),
                    // Thumbpad analog (CD-i remote / later pads) — routes to left-stick axis
                    new("Analog Up",    "Analog Up",    140,  90, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Down",  "Analog Down",  140, 130, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Left",  "Analog Left",  120, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                    new("Analog Right", "Analog Right", 160, 110, ButtonType.AnalogDirection, 30, 30, GAnalog),
                }
            },

            // ── Arcade ────────────────────────────────────────────────────────
            ["Arcade"] = new ControllerDefinition
            {
                Name = "Arcade",
                ControllerImage = "/Assets/images/Arcade/arcade_panel.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",       "Up",       140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",     "Down",     140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",     "Left",     100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",    "Right",    180, 110, ButtonType.DPad,   70, 70, GDPad),
                    // FBNeo "Classic" libretro order: JOYPAD_Y(1)=Btn1, JOYPAD_B(0)=Btn2, etc.
                    new("Button 1", "Y",        300, 130, ButtonType.Button, 35, 35, GFace),
                    new("Button 2", "B",        340, 110, ButtonType.Button, 35, 35, GFace),
                    new("Button 3", "X",        380, 130, ButtonType.Button, 35, 35, GFace),
                    new("Button 4", "A",        420, 110, ButtonType.Button, 35, 35, GFace),
                    new("Button 5", "L",        300, 160, ButtonType.Button, 35, 35, GFace),
                    new("Button 6", "R",        340, 140, ButtonType.Button, 35, 35, GFace),
                    new("Button 7", "L2",       380, 160, ButtonType.Button, 35, 35, GFace),
                    new("Button 8", "R2",       420, 140, ButtonType.Button, 35, 35, GFace),
                    new("Coin",     "Select",   240, 200, ButtonType.Button, 50, 20, GSystem),
                    new("Start",    "Start",    320, 200, ButtonType.Button, 50, 20, GSystem),
                }
            },

            ["3DO"] = new ControllerDefinition
            {
                Name = "3DO",
                ControllerImage = "/Assets/images/3DO/controller_3do@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   70, 70, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   70, 70, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   70, 70, GDPad),
                    new("C",      "A",      420, 130, ButtonType.Button, 35, 35, GFace),
                    new("B",      "B",      380, 170, ButtonType.Button, 35, 35, GFace),
                    new("A",      "Y",      340, 130, ButtonType.Button, 35, 35, GFace),
                    new("X",      "X",      380,  90, ButtonType.Button, 35, 35, GFace),
                    new("L",      "L",       60,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("R",      "R",      430,  30, ButtonType.Button, 70, 25, GShoulder),
                    new("P",      "Start",  285, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Left Analog Up",    "LeftAnalogUp",    140,  50, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Down",  "LeftAnalogDown",  140,  90, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Left",  "LeftAnalogLeft",  120,  70, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                    new("Left Analog Right", "LeftAnalogRight", 160,  70, ButtonType.AnalogDirection, 30, 30, GLAnalog),
                }
            },
        };

        public static ControllerDefinition? GetControllerDefinition(string consoleName)
            => AllControllers.TryGetValue(consoleName, out var def) ? def : null;

        public static List<(string Tag, string Name)> GetSupportedConsoles()
        {
            var list = new List<(string Tag, string Name)>();
            foreach (var kvp in AllControllers)
                list.Add((Tag: kvp.Key, Name: kvp.Value.Name));
            list.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }
}
