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

            // ── Atari ─────────────────────────────────────────────────────────
            ["Atari2600"] = new ControllerDefinition
            {
                Name = "Atari 2600",
                ControllerImage = "/Assets/images/Atari 2600/controller_2600@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",    "Up",    140,  70, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",  "Down",  140, 150, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",  "Left",  100, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Right", "Right", 180, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Fire",  "B",     380, 140, ButtonType.Button, 35, 35, GFace),
                }
            },
            ["Atari5200"] = new ControllerDefinition
            {
                Name = "Atari 5200",
                ControllerImage = "/Assets/images/Atari 5200/controller_5200@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   60, 60, "Joystick"),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   60, 60, "Joystick"),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   60, 60, "Joystick"),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   60, 60, "Joystick"),
                    new("Fire 1", "B",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Fire 2", "Y",      340, 140, ButtonType.Button, 35, 35, GFace),
                    new("Pause",  "Start",  260, 190, ButtonType.Button, 45, 20, GSystem),
                    new("Reset",  "Select", 210, 190, ButtonType.Button, 45, 20, GSystem),
                }
            },
            ["Atari7800"] = new ControllerDefinition
            {
                Name = "Atari 7800",
                ControllerImage = "/Assets/images/Atari 7800/controller_7800@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",     "Up",     140,  70, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",   "Down",   140, 150, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",   "Left",   100, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",  "Right",  180, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Fire 1", "B",      380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Fire 2", "Y",      340, 140, ButtonType.Button, 35, 35, GFace),
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
            ["Intellivision"] = new ControllerDefinition
            {
                Name = "Intellivision",
                ControllerImage = "/Assets/images/Intellivision/controller_intellivision@2x.png",
                Buttons = new List<ButtonDefinition>
                {
                    new("Up",        "Up",    140,  70, ButtonType.DPad,   60, 60, GDPad),
                    new("Down",      "Down",  140, 150, ButtonType.DPad,   60, 60, GDPad),
                    new("Left",      "Left",  100, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Right",     "Right", 180, 110, ButtonType.DPad,   60, 60, GDPad),
                    new("Top",       "A",     380, 120, ButtonType.Button, 35, 35, GFace),
                    new("Left Side", "B",     340, 140, ButtonType.Button, 35, 35, GFace),
                    new("Right Side","Y",     420, 140, ButtonType.Button, 35, 35, GFace),
                    new("1",         "L",     180, 220, ButtonType.Button, 30, 30, "Keypad"),
                    new("2",         "R",     220, 220, ButtonType.Button, 30, 30, "Keypad"),
                    new("3",         "X",     260, 220, ButtonType.Button, 30, 30, "Keypad"),
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
