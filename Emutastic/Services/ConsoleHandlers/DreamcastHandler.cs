using System.Collections.Generic;
using System.IO;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for Sega Dreamcast via Flycast/Reicast libretro.
    ///
    /// VMU (save memory) initialization — critical for "No VMU Found" fix:
    ///
    ///  1. RETRO_ENVIRONMENT_SET_CONTROLLER_INFO (cmd=35): MUST return true.
    ///     Returning false causes Reicast to skip ALL sub-peripheral initialization,
    ///     so no VMU ever attaches and games see "No VMU Found".
    ///
    ///  2. RETRO_ENVIRONMENT_GET_RUMBLE_INTERFACE (cmd=23): MUST supply a function
    ///     pointer (even a no-op). Returning false also blocks sub-peripheral setup.
    ///
    ///  3. Core options reicast_device_portN_slot1 = "VMU" must be pre-seeded before
    ///     retro_load_game. The core reads these during maple bus reconfiguration.
    ///
    ///  4. retro_set_controller_port_device must be called for all 4 ports (not just
    ///     port 0). This triggers a full maple bus reconfiguration that attaches the
    ///     VMU sub-peripherals for every port.
    ///
    ///  5. system/dc/ directory must exist before load — the core auto-creates
    ///     vmu_save_A1.bin etc. there. Do NOT pre-create VMU files; the core uses its
    ///     own zlib-compressed format and pre-created files block auto-creation and
    ///     produce "No VMU Found".
    ///
    /// Option prefix: this build uses "reicast_" (not "flycast_").
    /// </summary>
    public class DreamcastHandler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "Dreamcast";
        public override bool UsesAnalogStick => true;

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            ["reicast_internal_resolution"] = "640x480",
            ["reicast_widescreen_cheats"]   = "disabled",
            ["reicast_enable_dsp"]          = "enabled",
            ["reicast_per_content_vmus"]    = "disabled",
            ["reicast_device_port1_slot1"]  = "VMU",
            ["reicast_device_port2_slot1"]  = "VMU",
            ["reicast_device_port3_slot1"]  = "VMU",
            ["reicast_device_port4_slot1"]  = "VMU",
        };

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            // Call for all 4 ports — triggers full maple bus reconfiguration
            // that attaches VMU sub-peripherals for each port.
            for (uint port = 0; port < 4; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        public override int PreferredHwContext => 3;  // RETRO_HW_CONTEXT_OPENGL_CORE
        public override bool AllowHwSharedContext => false;
        public override bool UseEmbeddedWindow => false;
        public override bool UseGLOverlay => true;

        public override string ResolveSystemDirectory(string defaultDir, string coreDllDir)
        {
            string cleanDir = defaultDir.TrimEnd(Path.DirectorySeparatorChar,
                                                  Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(Path.Combine(cleanDir, "dc"));
            return cleanDir;
        }

        public override void PrepareSaveDirectory(string saveDir)
        {
            // Do NOT pre-create VMU files — core auto-creates with its own zlib format.
            // Just ensure the dc/ sub-folder exists so the core can write there.
            Directory.CreateDirectory(Path.Combine(saveDir, "dc"));
        }
    }
}
