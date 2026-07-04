using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Emutastic.Models;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for PlayStation 1 (Beetle PSX HW by default).
    /// Beetle PSX HW negotiates Vulkan via SET_HW_RENDER. Without a non-software
    /// context the HW core falls back to its built-in software renderer and the
    /// internal-resolution / PGXP / texture-filter options become no-ops.
    /// </summary>
    public class Ps1Handler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD    = 1;           // digital PlayStation Controller (SCPH-1080), reports SIO id 0x41
        private const uint RETRO_DEVICE_DUALSHOCK = (2 << 8) | 5; // RETRO_DEVICE_SUBCLASS(RETRO_DEVICE_ANALOG, 1) = 517, reports 0x73

        private readonly Game? _game;

        public Ps1Handler(Game? game = null) => _game = game;

        public override string ConsoleName => "PS1";
        public override bool UsesAnalogStick => true;

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            // Most PS1 games run best as a DualShock (d-pad + analog sticks). But a
            // handful of early titles read NO controller input at all — not even Start —
            // when the pad reports as an analog DualShock (SIO id 0x73); they only
            // recognise the original digital PlayStation Controller (0x41). SOTN is the
            // canonical example. For those, hand the core a digital pad so input works;
            // everyone else keeps analog. (Emulators like DuckStation force digital the
            // same way for these games.)
            uint device = IsDigitalOnly(_game?.Title) ? RETRO_DEVICE_JOYPAD : RETRO_DEVICE_DUALSHOCK;
            for (uint port = 0; port < 2; port++)
                core.SetControllerPortDevice(port, device);
        }

        // PS1 titles that must use the digital controller — the analog DualShock leaves
        // them completely unresponsive. Conservative: only games confirmed to *break*
        // under analog, not games that merely ignore the sticks. Stored as normalized
        // titles (see NormalizeTitle) so region/dump tags in the library name don't matter.
        // To extend: DuckStation's gamedb.yaml is the authoritative source — entries whose
        // supported_controllers omit AnalogController are the digital-only set.
        private static readonly HashSet<string> DigitalOnlyTitles = new(StringComparer.Ordinal)
        {
            "castlevania symphony of the night",
        };

        private static bool IsDigitalOnly(string? title)
            => !string.IsNullOrEmpty(title) && DigitalOnlyTitles.Contains(NormalizeTitle(title));

        // Lowercase, drop (parenthetical) / [bracketed] tags (region, dump flags, disc
        // numbers), reduce the rest to alphanumeric words, collapse whitespace. So
        // "Castlevania - Symphony of the Night (USA)" -> "castlevania symphony of the night".
        private static string NormalizeTitle(string title)
        {
            var sb = new StringBuilder(title.Length);
            int depth = 0;
            foreach (char c in title)
            {
                if (c == '(' || c == '[') { depth++; continue; }
                if (c == ')' || c == ']') { if (depth > 0) depth--; continue; }
                if (depth > 0) continue;
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // Request OpenGL Core context for Beetle PSX HW. The Vulkan path was
        // tried first but Beetle PSX HW's v1 create_device hands back a device
        // missing the features parallel-psx later dispatches against, producing
        // NULL-IP AVs in the render thread. OpenGL has a much simpler init
        // contract (context_reset gets a current GL context; the core renders
        // through it directly) and our OpenGL plumbing is mature from N64,
        // Dolphin, and 3DS. If the user has the SW mednafen_psx_libretro
        // selected instead, the core ignores HW context negotiation entirely
        // and runs as software — nothing to harm there.
        public override int PreferredHwContext => 3; // RETRO_HW_CONTEXT_OPENGL_CORE

        // Use the GL overlay window for direct GPU→GPU presentation. Without
        // this, OnVideoRefresh falls through to the readback-via-glReadPixels
        // path that ships ~78 MB per frame across PCIe at 8× internal
        // resolution (5120×3824×4 bytes), then Marshal-copies it into a WPF
        // WriteableBitmap on the UI thread — frame-dropping pipeline even on
        // top-tier hardware. With overlay = true the core's FBO blits directly
        // to a native HWND backbuffer via glBlitFramebuffer + SwapBuffers and
        // the WPF compositor never touches the upscaled image. Same pipeline
        // GameCube Dolphin uses (and Dreamcast Flycast).
        // Falls back to the readback path when the AMD/Intel compatibility
        // toggle is on, since that mode renders directly to FBO 0 and the
        // overlay path needs a separate FBO to blit from.
        public override bool UseGLOverlay => !UseDefaultFramebuffer;

        // AMD/Intel GL drivers misbehave when binding non-zero FBOs (the same
        // bottom-left rendering bug Dolphin hits) — when the user has opted
        // into the global compatibility mode, render directly to FBO 0.
        public override bool UseDefaultFramebuffer =>
            App.Configuration?.GetEmulatorConfiguration().ResolveAmdIntelCompat() ?? false;

        public override List<(string key, string label)> GetVisualOptions() => new()
        {
            ("beetle_psx_hw_internal_resolution", "Internal Resolution"),
            ("beetle_psx_hw_filter", "Texture Filter"),
            ("beetle_psx_hw_msaa", "Anti-Aliasing"),
            ("beetle_psx_hw_depth", "Color Depth"),
        };

        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            // Force the OpenGL HW renderer to match our negotiated GL context.
            // `hardware` would let the core pick either backend; pinning to
            // `hardware_gl` avoids the core silently selecting Vulkan and
            // failing back to software when our context isn't compatible.
            ["beetle_psx_hw_renderer"] = "hardware_gl",
            // software_fb left at core default (enabled). Some games (Spyro,
            // FF8 battles, etc.) read/write the PS1 framebuffer directly for
            // ground textures, pause menus, and screen transitions. The SW FB
            // path composites those at native resolution — disabling it breaks
            // those effects. Users who want pure HW rendering can toggle it
            // per-game in core preferences.
            // Sync CD access — the async path loses the CDC's disc handle on
            // retro_unserialize (Beetle PSX HW issue #297), causing every
            // disc-streaming game (FF8 notably) to freeze on the first read
            // after load. sync survives state restore reliably.
            ["beetle_psx_hw_cd_access_method"] = "sync",
            // Visual fidelity options (internal_resolution, PGXP, filter,
            // dither, MSAA, depth) are intentionally left at the core's
            // native-PSX defaults — output looks like real hardware out of
            // the box. Users who want upscaling/PGXP turn those on per-game
            // in core options.
        };
    }
}
