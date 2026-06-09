using System.Collections.Generic;
using System.Linq;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for PlayStation 2 (LRPS2 / pcsx2_libretro).
    ///
    /// Renderer is pinned to the **D3D11** GS backend — the path proven
    /// full-speed at high resolution on this rig (via RetroArch) and the one
    /// Emutastic's new D3D11 HW-render context drives (see D3D11Context +
    /// project_emutastic_ps2_d3d11_design). NEVER "Auto": Auto crashes the core
    /// on non-D3D frontend drivers (libretro/ps2 #13). Vulkan/paraLLEl-GS and
    /// OpenGL are deferred (cross-platform follow-up).
    /// </summary>
    public class Ps2Handler : ConsoleHandlerBase
    {
        // LRPS2's retro_set_controller_port_device only recognizes plain
        // RETRO_DEVICE_JOYPAD (=1), which it maps to a DualShock2 (and still
        // reads the analog axes). The DualShock SUBCLASS (2<<8|5 = 517) falls
        // through its switch to "Type = None" — i.e. NO controller connected,
        // which blocks games like God of War that gate on a detected pad.
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "PS2";
        public override bool UsesAnalogStick => true;

        public override void ConfigureControllerPorts(LibretroCore core)
        {
            for (uint port = 0; port < 2; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        // NOTE: this hook feeds BOTH the in-game cog dropdown AND the launch-time
        // value validation/clamp — so capping a value here silently overrides what
        // the user set in Preferences → Core Options. Keep it to genuinely
        // unsupported values only (we don't gate internal resolution here; high
        // upscales are allowed and just cost VRAM).
        public override string[] FilterCoreOptionValues(string key, string[] values)
        {
            // Renderer: expose only the two we support — D3D11 (stable default) and
            // paraLLEl-GS (Vulkan). Hide Auto/D3D12/standard-Vulkan/OpenGL/Software.
            // D3D11 is first so it stays the validation fallback.
            if (key == "pcsx2_renderer")
                return values.Where(v => v == "D3D11" || v == "paraLLEl-GS").ToArray();

            return values;
        }

        // RETRO_HW_CONTEXT_D3D11. EmulatorWindow's SET_HW_RENDER creates the
        // D3D11Context and hands the core our device via GET_HW_RENDER_INTERFACE.
        public override int PreferredHwContext => 7;

        // Upscale lives in the cog's Visuals menu. The panel filters to keys the
        // active core actually announced, so this is safe even before the core
        // declares it.
        // Renderer-aware: the two renderers use DIFFERENT resolution controls.
        // GSdx HW (D3D11) uses pcsx2_upscale_multiplier; parallel-GS IGNORES that and
        // uses its own supersampling (pcsx2_pgs_ssaa) + high-res scanout. Showing the
        // upscale option on parallel-GS is misleading — it does nothing — so swap the
        // visible control based on the selected renderer.
        public override List<(string key, string label)> GetVisualOptions(IReadOnlyDictionary<string, string> coreOptions)
        {
            bool pgs = coreOptions != null
                       && coreOptions.TryGetValue("pcsx2_renderer", out var r)
                       && r == "paraLLEl-GS";

            var list = new List<(string key, string label)>
            {
                // D3D11 (default) = rock-solid, Windows-only. paraLLEl-GS = Vulkan
                // compute renderer (no shader hitch, ports to Linux), experimental.
                ("pcsx2_renderer", "Renderer ⚠ restart"),
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
        // (project_emutastic_ps2_integration). Renderer EXPLICIT D3D11. Users
        // override via the core-options UI; persisted values win over these.
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
    }
}
