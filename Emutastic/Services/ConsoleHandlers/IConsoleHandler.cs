using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Encapsulates all console-specific behaviour so EmulatorWindow stays generic.
    /// Each console gets its own implementation; only that file needs to change when
    /// debugging or adding support for a new console.
    /// </summary>
    public interface IConsoleHandler
    {
        string ConsoleName { get; }

        /// <summary>True for consoles whose cores expect analog stick input (N64, GameCube, PS1, etc.).</summary>
        bool UsesAnalogStick { get; }

        /// <summary>Core options to pre-seed before the core announces its variable list.</summary>
        Dictionary<string, string> GetDefaultCoreOptions();

        /// <summary>
        /// Called after retro_load_game to configure controller ports.
        /// Default: sets port 0 to JOYPAD. GameCube overrides to set all 4 ports.
        /// </summary>
        void ConfigureControllerPorts(LibretroCore core);

        /// <summary>
        /// Called once per option key during RETRO_ENVIRONMENT_SET_VARIABLES so each
        /// handler can inject console-specific values that weren't pre-seeded.
        /// Implementations may mutate <paramref name="coreOptions"/> directly.
        /// </summary>
        void OnVariableAnnounced(string key, string[] validValues, Dictionary<string, string> coreOptions);

        /// <summary>
        /// Returns the display aspect ratio to use. Return 0 to fall back to the
        /// core-reported value. TG16 overrides to force 4:3 regardless of core geometry.
        /// </summary>
        double GetDisplayAspectRatio(uint baseWidth, uint baseHeight, float coreAspectRatio);

        /// <summary>
        /// Override the emulation loop's target fps regardless of what the core reports via
        /// retro_get_system_av_info. Return -1 to use the core-reported value (default).
        /// Dreamcast returns 60 because the DC hardware always runs at 60Hz VBL; Flycast
        /// reports the game's rendered fps (30 for Hydro Thunder etc.) which, if used as
        /// the retro_run rate, halves the VBL interrupt frequency and makes the game run
        /// at half speed.
        /// </summary>
        double HardwareTargetFps { get; }

        /// <summary>Called immediately before retro_hw_context_reset is invoked.</summary>
        void OnBeforeContextReset();

        /// <summary>Called immediately after retro_hw_context_reset returns.</summary>
        void OnAfterContextReset();

        /// <summary>
        /// Value to write for RETRO_ENVIRONMENT_GET_PREFERRED_HW_RENDER.
        /// Return -1 to not handle the call (core uses its own default).
        /// GameCube returns RETRO_HW_CONTEXT_OPENGL_CORE (3).
        /// All other consoles return -1 so software cores stay software.
        /// </summary>
        int PreferredHwContext { get; }

        /// <summary>
        /// Whether to return true for RETRO_ENVIRONMENT_SET_HW_SHARED_CONTEXT.
        /// Dolphin and parallel-n64 both need this — their EmuThreads create shared GL contexts.
        /// </summary>
        bool AllowHwSharedContext { get; }

        /// <summary>
        /// Whether to embed a real Win32 HwndHost window in the WPF layout for rendering.
        /// True only for Dolphin, which renders directly to window FBO 0 via SwapBuffers.
        /// All other HW-render cores (N64 etc.) use a hidden offscreen window and glReadPixels.
        /// </summary>
        bool UseEmbeddedWindow { get; }

        /// <summary>
        /// Returns the system directory path the core should receive via
        /// RETRO_ENVIRONMENT_GET_SYSTEM_DIRECTORY. By default returns <paramref name="defaultDir"/>.
        /// GameCube overrides to return <paramref name="coreDllDir"/> so that
        /// dolphin-emu\Sys\ can be placed alongside the DLL rather than in AppData.
        /// </summary>
        string ResolveSystemDirectory(string defaultDir, string coreDllDir);

        /// <summary>
        /// Called after the save/battery directory is created. Allows a handler to
        /// create required sub-directories or perform other pre-load setup.
        /// Dreamcast overrides this to create the dc/ sub-folder the core expects.
        /// </summary>
        void PrepareSaveDirectory(string saveDir);

        /// <summary>
        /// When true, the readback path reads the entire FBO (fboWidth × fboHeight) rather
        /// than the dimensions reported by retro_video_refresh. Use for cores like vecx that
        /// render game content across the full square FBO and rely on aspect_ratio for display.
        /// Most HW cores render at exactly base_width × base_height, so the default is false.
        /// </summary>
        bool UseFullFboReadback { get; }
    }
}
