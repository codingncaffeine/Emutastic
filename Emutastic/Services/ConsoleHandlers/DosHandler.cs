using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// DOSBox Pure handler. Software-rendered by default; Voodoo path would
    /// flip PreferredHwContext to GL and register a HW callback.
    /// </summary>
    public class DosHandler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD   = 1;
        private const uint RETRO_DEVICE_MOUSE    = 2;
        private const uint RETRO_DEVICE_KEYBOARD = 3;

        public override string ConsoleName => "DOS";

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            // Show Start Menu for 3s on first launch, skip once user picks an EXE.
            { "dosbox_pure_menu_time",          "3" },
            // Auto cycles works for the overwhelming majority of DOS games.
            { "dosbox_pure_cycles",             "auto" },
            // Sensible defaults — users override per-game later.
            { "dosbox_pure_memory_size",        "16" },
            { "dosbox_pure_machine",            "svga" },
            { "dosbox_pure_sblaster_type",      "sb16" },
            { "dosbox_pure_aspect_correction",  "false" },
            { "dosbox_pure_savestate",          "on" },
            { "dosbox_pure_on_screen_keyboard", "true" },
        };

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            // Port 0 = keyboard (primary DOS input), Port 1 = mouse, Port 2+ = gamepad.
            core.SetControllerPortDevice(0, RETRO_DEVICE_KEYBOARD);
            core.SetControllerPortDevice(1, RETRO_DEVICE_MOUSE);
            core.SetControllerPortDevice(2, RETRO_DEVICE_JOYPAD);
            core.SetControllerPortDevice(3, RETRO_DEVICE_JOYPAD);
        }
    }
}
