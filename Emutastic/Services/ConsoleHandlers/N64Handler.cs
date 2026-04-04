using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for N64 / Parallel N64.
    /// glide64 (OpenGL GPU-accelerated) is the default for playable performance.
    /// User can switch to angrylion (software, accurate) or rice (fast) via preferences.
    /// </summary>
    public class N64Handler : ConsoleHandlerBase
    {
        public override string ConsoleName => "N64";
        public override bool UsesAnalogStick => true;

        // parallel-n64 is built on mupen64plus which ALWAYS spawns an internal EmuThread,
        // even for software plugins like angrylion.  That EmuThread needs its own GL context.
        // Returning true for SET_HW_SHARED_CONTEXT tells mupen64plus to create a shared
        // context on its EmuThread that shares objects (textures, FBOs) with ours.
        // We point get_current_framebuffer at our own FBO so the EmuThread renders into it,
        // then glReadPixels from the shared FBO inside the video callback.
        // UseEmbeddedWindow stays false — we use a hidden offscreen window, not HwndHost.
        // false: glide64 runs on our emu thread (same context as context_reset and readback).
        // Using true puts rendering on mupen64plus's EmuThread with a separate GL context;
        // context_reset's GL objects (FBOs etc.) aren't visible there → black screen.
        public override bool AllowHwSharedContext => false;

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            ["parallel-n64-gfxplugin"]             = "glide64",
            ["parallel-n64-cpucore"]               = "dynamic_recompiler",
            ["parallel-n64-disable_expmem"]        = "disabled",
            ["parallel-n64-framerate"]             = "fullspeed",
            ["parallel-n64-angrylion-sync"]        = "Low",
            ["parallel-n64-angrylion-vioverlay"]   = "Filtered",
            ["parallel-n64-angrylion-multithread"] = "all threads",
            ["parallel-n64-angrylion-overscan"]    = "disabled",
            ["parallel-n64-audio-buffer-size"]     = "2048",
            ["parallel-n64-pak1"]                  = "memory",
            ["parallel-n64-pak2"]                  = "none",
            ["parallel-n64-pak3"]                  = "none",
            ["parallel-n64-pak4"]                  = "none",
            ["parallel-n64-astick-deadzone"]       = "15",
            ["parallel-n64-astick-sensitivity"]    = "100",
            ["parallel-n64-gfxplugin-accuracy"]    = "low",
            ["parallel-n64-screensize"]            = "640x480",
            ["parallel-n64-aspectratiohint"]       = "normal",
            ["parallel-n64-filtering"]             = "automatic",
            ["parallel-n64-virefresh"]             = "auto",
            ["parallel-n64-bufferswap"]            = "disabled",
            ["parallel-n64-alt-map"]               = "disabled",
        };
    }
}
