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

        /// <summary>
        /// When true, OnInputState falls back to the left analog stick when
        /// no digital JOYPAD direction is pressed, mapping stick UP/DOWN/LEFT/
        /// RIGHT to the corresponding JOYPAD_UP/DOWN/LEFT/RIGHT button. Used
        /// for consoles whose hardware was digital-only (CDi, Arcade) but
        /// whose users naturally drive movement from the modern controller's
        /// analog thumbstick. Diagonals survive because the X and Y deadzone
        /// checks fire independently in ControllerManager.
        /// </summary>
        bool PromoteAnalogStickToDpad { get; }

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

        /// <summary>
        /// When true, use a WS_POPUP overlay window with glBlitFramebuffer + SwapBuffers
        /// instead of glReadPixels CPU readback. Eliminates the GPU→CPU→GPU round-trip
        /// that bottlenecks HW cores at high internal resolutions.
        /// </summary>
        bool UseGLOverlay { get; }

        /// <summary>
        /// When true, return framebuffer 0 from get_current_framebuffer instead of
        /// the frontend's own FBO, and read pixels back from FBO 0. Some OpenGL
        /// cores (Dolphin libretro on AMD/Intel GL drivers) misrender into a
        /// frontend-supplied FBO; rendering directly to the default backbuffer
        /// sidesteps this. Cost: the in-game GL overlay is incompatible with
        /// this mode, so it's disabled at runtime when UseDefaultFramebuffer is on.
        /// Default false; opt-in per console (currently exposed for GameCube only).
        /// </summary>
        bool UseDefaultFramebuffer { get; }

        /// <summary>
        /// Lets a handler filter the valid values a core advertises for a given core option,
        /// before they are shown in the options UI. Default returns the list unchanged. GameCube
        /// uses it to drop the 1x/2x Internal Resolution values, which trigger dolphin_libretro's
        /// low-resolution framebuffer-indirection bug (game renders into the lower-left corner).
        /// </summary>
        string[] FilterCoreOptionValues(string key, string[] values);

        /// <summary>
        /// Returns the curated visual options to surface in the in-game cog overlay.
        /// Each tuple is (core option key, display label). Keys are validated at
        /// runtime against the loaded core's option set — missing keys are skipped.
        /// Return empty list for consoles with no HW visual options.
        /// </summary>
        List<(string key, string label)> GetVisualOptions();
    }
}
