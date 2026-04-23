using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// DOSBox Pure handler. Default path is software-rendered. When the user
    /// has Voodoo enabled (the DBP default) and <c>dosbox_pure_voodoo_perf</c>
    /// set to <c>auto</c> or <c>4</c> (hardware OpenGL), Emutastic flips to
    /// the HW OpenGL pipeline so DBP can render 3dfx titles at full speed via
    /// <c>dosbox_pure_voodoo_scale</c> (1×–8×).
    ///
    /// All HW-specific flags live on this handler; no other console is touched.
    /// </summary>
    public class DosHandler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD   = 1;
        private const uint RETRO_DEVICE_MOUSE    = 2;
        private const uint RETRO_DEVICE_KEYBOARD = 3;

        public override string ConsoleName => "DOS";

        /// <summary>
        /// Set by <c>EmulatorWindow.SeedDefaultCoreOptions</c> after the user's
        /// core-option values are loaded. Controls whether Emutastic pre-creates
        /// an OpenGL context for DBP's 3dfx Voodoo hardware-rendering path.
        /// </summary>
        public bool UseVoodooOpenGL { get; set; }

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

        // =====================================================================
        // HW OpenGL (3dfx Voodoo) path — gated by UseVoodooOpenGL
        // =====================================================================

        // RETRO_HW_CONTEXT_OPENGL_CORE = 3. DBP requests Core 3.1 (or falls
        // back to 2.0) when voodoo_perf is auto / OpenGL.  If Voodoo is off or
        // set to software perf modes, return -1 so Emutastic skips HW init.
        public override int PreferredHwContext => UseVoodooOpenGL ? 3 : -1;

        // DBP's Draw() runs on the retro_run thread (same as GameCube / Dolphin
        // single-threaded mode).  It uses our get_current_framebuffer() and
        // calls video_cb(RETRO_HW_FRAME_BUFFER_VALID, ...) from the same thread
        // so the context stays current — no shared-context handoff needed.
        public override bool AllowHwSharedContext => false;

        // DBP does not want an embedded HwndHost; it renders to whatever FBO
        // we bind via get_current_framebuffer.
        public override bool UseEmbeddedWindow => false;

        // GL overlay blit path = zero-CPU presentation.  DBP writes with
        // bottom_left_origin=true, and the overlay's back buffer is also
        // bottom-left, so glBlitFramebuffer presents correctly without a flip.
        public override bool UseGLOverlay => UseVoodooOpenGL;

        // DBP reports exact view_width/view_height to video_cb — read the
        // callback dimensions, not the full FBO.
        public override bool UseFullFboReadback => false;
    }
}
