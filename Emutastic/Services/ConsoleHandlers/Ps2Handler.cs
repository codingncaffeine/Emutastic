using System;
using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for PlayStation 2 (LRPS2 / pcsx2_libretro).
    ///
    /// Two GS backends are offered: **D3D11** (the stable default, Windows-only,
    /// driven by our D3D11 HW-render context) and **OpenGL** (cross-platform,
    /// driven through the same mature GL overlay path PS1/Dreamcast/GameCube use —
    /// SET_HW_RENDER type 3). The HW context type follows the selected renderer.
    /// NEVER "Auto": Auto crashes the core on non-D3D frontend drivers
    /// (libretro/ps2 #13). Vulkan / paraLLEl-GS is hidden for now — it's prone to
    /// native GS-register crashes; flip <see cref="ExposeParallelGs"/> to bring it
    /// back as an experimental option once it matures.
    /// </summary>
    public class Ps2Handler : ConsoleHandlerBase
    {
        // LRPS2's retro_set_controller_port_device only recognizes plain
        // RETRO_DEVICE_JOYPAD (=1), which it maps to a DualShock2 (and still
        // reads the analog axes). The DualShock SUBCLASS (2<<8|5 = 517) falls
        // through its switch to "Type = None" — i.e. NO controller connected,
        // which blocks games like God of War that gate on a detected pad.
        private const uint RETRO_DEVICE_JOYPAD = 1;

        // Vulkan/paraLLEl-GS is hidden until it stabilises. Set to true to expose
        // it again as an experimental renderer option (its pgs-specific Visuals
        // entries below come back with it).
        private const bool ExposeParallelGs = false;

        // OpenGL is hidden while its LRPS2 teardown deadlock is unsolved:
        // retro_unload_game's cpu_thread.join() never returns (MTGS/GS shutdown),
        // so every close leaks a live zombie core thread and switching games stacks
        // them until the single-instance core corrupts and crashes. D3D11 is fully
        // stable. Flip to true to re-expose OpenGL for testing a teardown fix.
        public static readonly bool ExposeOpenGl = false;

        public override string ConsoleName => "PS2";
        public override bool UsesAnalogStick => true;

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            for (uint port = 0; port < 2; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        // The selected GS backend (persisted via Core Options). Drives the HW
        // context type and presentation flags so D3D11 and OpenGL each take the
        // right path. Renderer is restart-required, so reading it per-launch is
        // consistent for the life of a session.
        private static bool RendererIsOpenGl()
        {
            if (!ExposeOpenGl) return false;   // OpenGL hidden → always the D3D11 path
            try
            {
                var vals = new CoreOptionsService().LoadValues("pcsx2_libretro");
                return vals.TryGetValue("pcsx2_renderer", out var r)
                    && r.Equals("OpenGL", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // NOTE: this hook feeds BOTH the in-game cog dropdown AND the launch-time
        // value validation/clamp — so capping a value here silently overrides what
        // the user set in Preferences → Core Options. A stale "paraLLEl-GS" value
        // therefore clamps to the first allowed (D3D11) while that backend is hidden.
        public override string[] FilterCoreOptionValues(string key, string[] values)
        {
            // Renderer: expose D3D11 (stable default) + OpenGL (cross-platform).
            // D3D11 stays first so it's the validation fallback. paraLLEl-GS only
            // when explicitly re-enabled. Hide Auto/D3D12/standard-Vulkan/Software.
            if (key == "pcsx2_renderer")
                return values.Where(v => v == "D3D11"
                                      || (ExposeOpenGl && v == "OpenGL")
                                      || (ExposeParallelGs && v == "paraLLEl-GS")).ToArray();

            // Internal resolution: cap at 6x Native (~4K). Beyond 6x, LRPS2 reports
            // BASE geometry instead of the upscaled size, so our render FBO never
            // sizes up and the screen goes black. 1x–6x are verified good on both
            // D3D11 and OpenGL. (EmulatorWindow's clamp sends a stale >6x value to
            // the highest remaining option, i.e. 6x.)
            if (key == "pcsx2_upscale_multiplier")
                return values.Where(v =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(v, @"^(\d+)x");
                    return !m.Success || (int.TryParse(m.Groups[1].Value, out int n) && n <= 6);
                }).ToArray();

            return values;
        }

        // D3D11 → RETRO_HW_CONTEXT_D3D11 (7); EmulatorWindow's SET_HW_RENDER builds
        // the D3D11Context and hands the core our device via GET_HW_RENDER_INTERFACE.
        // OpenGL → RETRO_HW_CONTEXT_OPENGL_CORE (3); the core renders pcsx2's OpenGL
        // GS through the GL overlay context, identical to PS1/Dreamcast/GameCube.
        public override int PreferredHwContext => RendererIsOpenGl() ? 3 : 7;

        // GL overlay flags only matter on the OpenGL path; D3D11 presents through
        // its own swapchain context (both false there). Mirrors Ps1Handler.
        public override bool UseGLOverlay => RendererIsOpenGl() && !UseDefaultFramebuffer;

        // AMD/Intel GL drivers misbehave binding non-zero FBOs — render to FBO 0
        // when the global compatibility toggle is on (OpenGL path only).
        public override bool UseDefaultFramebuffer =>
            RendererIsOpenGl()
            && (App.Configuration?.GetEmulatorConfiguration().ResolveAmdIntelCompat() ?? false);

        // Upscale lives in the cog's Visuals menu. Both D3D11 and OpenGL are GSdx
        // HW backends that use pcsx2_upscale_multiplier, so that's the resolution
        // control. (paraLLEl-GS, when re-enabled, uses its own SSAA/scanout pair.)
        public override List<(string key, string label)> GetVisualOptions(IReadOnlyDictionary<string, string> coreOptions)
        {
            bool pgs = ExposeParallelGs && coreOptions != null
                       && coreOptions.TryGetValue("pcsx2_renderer", out var r)
                       && r == "paraLLEl-GS";

            var list = new List<(string key, string label)>
            {
                ("pcsx2_renderer", "Renderer · OpenGL is cross-platform ⚠ restart"),
            };

            if (pgs)
            {
                list.Add(("pcsx2_pgs_ssaa", "Supersampling ⚠ restart"));
                list.Add(("pcsx2_pgs_high_res_scanout", "High-Res Scanout ⚠ restart"));
            }
            else
            {
                list.Add(("pcsx2_upscale_multiplier", "Internal Resolution ⚠ restart"));
            }
            return list;
        }

        // Sane desktop defaults condensed from the LRPS2 integration brief
        // (project_emutastic_ps2_integration). Renderer EXPLICIT D3D11 (stable
        // default). Users override via the core-options UI; persisted values win.
        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            ["pcsx2_renderer"]            = "D3D11",
            ["pcsx2_upscale_multiplier"]  = "2x Native (~720p)",   // must match the core's exact value string
            ["pcsx2_fastboot"]            = "enabled",
            ["pcsx2_fastcdvd"]            = "disabled",
            ["pcsx2_shared_memory_cards"] = "enabled",
            ["pcsx2_widescreen_hint"]     = "disabled",
            ["pcsx2_deinterlace_mode"]    = "Automatic",
            ["pcsx2_texture_filtering"]   = "Bilinear (PS2)",
            ["pcsx2_blending_accuracy"]   = "Basic",
            ["pcsx2_ee_cycle_rate"]       = "100%",
            ["pcsx2_ee_cycle_skip"]       = "disabled",
            ["pcsx2_enable_hw_hacks"]     = "disabled",
            ["pcsx2_nointerlacing_hint"]  = "enabled",
            ["pcsx2_pcrtc_antiblur"]      = "enabled",
            ["pcsx2_dithering"]           = "Unscaled",
            ["pcsx2_anisotropic_filtering"] = "disabled",
        };

        /// <summary>
        /// LRPS2 defaults <c>pcsx2_bios</c> to the first file its folder scan
        /// returns (alphabetically the oldest Japanese dump) and ignores region.
        /// Pick a region-appropriate, newest redump dump from System/pcsx2/bios.
        /// Returns null when no <c>ps2-####{region}</c> dump is present so the
        /// core keeps its own default. Recognises the redump naming convention
        /// (<c>ps2-VVVVr-date.bin</c>, r = a/e/j region letter); other filenames
        /// (e.g. SCPH-named dumps) are left to the core.
        /// </summary>
        public static string? ResolveRegionBios(string? romPath)
        {
            if (string.IsNullOrEmpty(romPath)) return null;

            string biosDir = System.IO.Path.Combine(AppPaths.GetFolder("System"), "pcsx2", "bios");
            if (!System.IO.Directory.Exists(biosDir)) return null;

            // game region → PCSX2 BIOS region letter
            char? want = RomService.DetectRegion(romPath) switch
            {
                "USA"    => 'a',
                "Europe" => 'e',
                "Japan"  => 'j',
                _        => (char?)null,   // World/Unknown → newest of any region
            };

            var rx = new System.Text.RegularExpressions.Regex(
                @"^ps2-(\d{4})([a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            string? regionBest = null; int regionVer = -1;
            string? anyBest    = null; int anyVer    = -1;

            try
            {
                foreach (string path in System.IO.Directory.EnumerateFiles(biosDir, "ps2-*.bin"))
                {
                    string name = System.IO.Path.GetFileName(path);
                    var m = rx.Match(name);
                    if (!m.Success || !int.TryParse(m.Groups[1].Value, out int ver)) continue;

                    if (ver > anyVer) { anyVer = ver; anyBest = name; }
                    if (want.HasValue
                        && char.ToLowerInvariant(m.Groups[2].Value[0]) == want.Value
                        && ver > regionVer)
                    { regionVer = ver; regionBest = name; }
                }
            }
            catch { return null; }

            // Region match wins; otherwise the newest dump of any region — still
            // strictly better than the core's oldest-first alphabetical default.
            return regionBest ?? anyBest;
        }
    }
}
