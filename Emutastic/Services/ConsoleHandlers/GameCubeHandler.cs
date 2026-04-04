using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Handler for GameCube via Dolphin libretro.
    ///
    /// Key differences from generic cores:
    ///  - Requires all 4 controller ports configured post-LoadGame (triggers Dolphin's input stage 2 init)
    ///  - dolphin_cpu_core must be auto-selected at runtime from the core's valid list (avoid JIT)
    ///  - D3D11 must be blocked during context_reset so Dolphin goes straight to OGL
    ///  - Analog sticks active
    /// </summary>
    public class GameCubeHandler : ConsoleHandlerBase
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public override string ConsoleName => "GameCube";
        public override bool UsesAnalogStick => true;

        // =====================================================================
        // D3D11 blocking state (static — survives across context_reset calls)
        // Prevents Dolphin from partially initializing D3D during context_reset,
        // which leaves global renderer objects NULL when it falls back to OGL.
        // =====================================================================
        private static byte[]? _d3dOrigBytes;
        private static IntPtr  _d3dPatchAddr;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize,
            uint flNewProtect, out uint lpflOldProtect);

        // =====================================================================
        // Core options
        // =====================================================================
        public override Dictionary<string, string> GetDefaultCoreOptions() => new()
        {
            // Single-threaded: CPU runs on retro_run thread, no shared GL context needed
            ["dolphin_main_cpu_thread"]        = "disabled",
            // No VEH-based MMIO tricks
            ["dolphin_fastmem"]                = "disabled",
            // No 4 GB arena reservation inside a .NET process
            ["dolphin_fastmem_arena"]          = "disabled",
            // dolphin_cpu_core: NOT pre-seeded — OnVariableAnnounced picks the safest
            // non-JIT value from the core's own valid list at runtime
            ["dolphin_dsp_jit"]                = "disabled",
            ["dolphin_dsp_enable_jit"]         = "disabled",
            ["dolphin_dsp_hle"]                = "enabled",
            ["dolphin_skip_gc_bios"]           = "enabled",
            // Force OpenGL — only a GL context is set up
            ["dolphin_gfx_backend"]            = "OGL",
            ["dolphin_renderer"]               = "Hardware",
            // 1x native resolution
            ["dolphin_efb_scale"]              = "1",

            // ── Performance options ──────────────────────────────────────────
            // EFB copies to texture instead of RAM: avoids expensive VRAM→RAM→VRAM roundtrip.
            // This alone can be worth 20-30% for GC titles that use EFB effects heavily.
            ["dolphin_efb_copy_method"]        = "Texture",
            ["dolphin_efb_copy_to_texture"]    = "enabled",   // alternate key name (core version dependent)
            // Disable CPU reads from EFB — expensive synchronous stall, most games don't need it.
            ["dolphin_efb_access_enable"]      = "disabled",
            ["dolphin_efb_access"]             = "disabled",  // alternate key name
            // GPU-side texture decode — offloads format conversion from CPU to GPU.
            ["dolphin_gpu_texture_decode"]     = "enabled",
            // No texture filtering at 1x — eliminates bilinear sampling overhead.
            ["dolphin_texture_filtering"]      = "Nearest",
            // No anisotropic filtering.
            ["dolphin_max_anisotropy"]         = "0",
            // No MSAA.
            ["dolphin_video_multisampling"]    = "disabled",
            ["dolphin_msaa"]                   = "disabled",
            // Widescreen hack adds a clip-space transform pass — skip it.
            ["dolphin_widescreen_hack"]        = "disabled",
        };

        // =====================================================================
        // Controller ports
        // =====================================================================
        public override void ConfigureControllerPorts(LibretroCore core)
        {
            // Dolphin's BootCore (called inside retro_load_game) expects all 4 ports
            // to receive retro_set_controller_port_device before retro_run. Without
            // this, the input subsystem is left partially initialised and retro_run crashes.
            for (uint port = 0; port < 4; port++)
                core.SetControllerPortDevice(port, RETRO_DEVICE_JOYPAD);
        }

        // =====================================================================
        // CPU core mode — toggle at runtime via UseJit property
        // =====================================================================

        /// <summary>
        /// When true, selects JIT64 for full-speed emulation.
        /// JIT64 requires fastmem=disabled and fastmem_arena=disabled (already set above)
        /// to avoid SEH chain conflicts with .NET's own exception handling.
        /// If the game crashes on launch, set this back to false.
        /// </summary>
        public bool UseJit { get; set; } = true;

        public override void OnVariableAnnounced(string key, string[] validValues,
            Dictionary<string, string> coreOptions)
        {
            if (key != "dolphin_cpu_core" || validValues.Length == 0)
                return;

            string? pick;
            if (UseJit)
            {
                // JIT64: native recompilation, ~5x faster than CachedInterpreter.
                // fastmem is already disabled above so the VEH/SEH conflict is avoided.
                pick = validValues.FirstOrDefault(v => v == "1")
                    ?? validValues.FirstOrDefault(v => v.IndexOf("jit64", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? validValues.FirstOrDefault(v => v.IndexOf("jit",   StringComparison.OrdinalIgnoreCase) >= 0);
            }
            else
            {
                // CachedInterpreter: safe fallback if JIT64 is unstable.
                pick = validValues.FirstOrDefault(v => v == "5")
                    ?? validValues.FirstOrDefault(v => v.IndexOf("cachedinterpreter",  StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? validValues.FirstOrDefault(v => v.IndexOf("cached interpreter", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? validValues.FirstOrDefault(v => v == "0")
                    ?? validValues.FirstOrDefault(v => v.IndexOf("interpreter",        StringComparison.OrdinalIgnoreCase) >= 0
                                                    && v.IndexOf("jit",                StringComparison.OrdinalIgnoreCase) <  0);
            }

            string selected = pick ?? validValues[0];
            coreOptions[key] = selected;
            System.Diagnostics.Trace.WriteLine($"[GameCubeHandler] dolphin_cpu_core SELECT (UseJit={UseJit}): '{selected}' from [{string.Join(", ", validValues)}]");
        }

        // =====================================================================
        // D3D11 blocking around context_reset
        // =====================================================================
        public override void OnBeforeContextReset() => BlockD3D11();
        public override void OnAfterContextReset()  => UnblockD3D11();

        // Dolphin needs OpenGL Core profile and requires shared context support.
        // RETRO_HW_CONTEXT_OPENGL_CORE = 3
        public override int PreferredHwContext => 3;
        // With dolphin_main_cpu_thread=disabled Dolphin renders on retro_run's thread (our
        // _emuThread).  There is no separate Dolphin EmuThread, so we must NOT release the
        // GL context after context_reset — it must stay current for get_current_framebuffer
        // and retro_run.  Setting false keeps the context current and skips the N64-style
        // wglMakeCurrent(Zero,Zero) release that caused ctx=0x0 on every GCF call.
        public override bool AllowHwSharedContext => false;
        // Dolphin creates its own EmuThread context against an internal DC, not ours,
        // so SwapBuffers on our HwndHost DC presents an empty back buffer (black screen).
        // Use the same FBO-0 readback path as N64 instead.
        public override bool UseEmbeddedWindow => false;

        // Use the DLL's parent directory as the system directory so that
        // dolphin-emu\Sys\ can be placed alongside dolphin_libretro.dll.
        public override string ResolveSystemDirectory(string defaultDir, string coreDllDir)
            => coreDllDir;

        private static void BlockD3D11()
        {
            try
            {
                IntPtr d3d11 = LoadLibrary("d3d11.dll");
                if (d3d11 == IntPtr.Zero)
                {
                    Debug.WriteLine("[GameCubeHandler] BlockD3D11: d3d11.dll not found");
                    return;
                }

                _d3dPatchAddr = GetProcAddress(d3d11, "D3D11CreateDevice");
                if (_d3dPatchAddr == IntPtr.Zero)
                {
                    Debug.WriteLine("[GameCubeHandler] BlockD3D11: D3D11CreateDevice not found");
                    return;
                }

                _d3dOrigBytes = new byte[6];
                Marshal.Copy(_d3dPatchAddr, _d3dOrigBytes, 0, 6);

                // x64: mov eax, 0x80004005 (E_FAIL); ret — returns immediately before any D3D init
                byte[] patch = { 0xB8, 0x05, 0x40, 0x00, 0x80, 0xC3 };
                if (!VirtualProtect(_d3dPatchAddr, (UIntPtr)6, 0x40 /* PAGE_EXECUTE_READWRITE */, out uint oldProtect))
                {
                    Debug.WriteLine("[GameCubeHandler] BlockD3D11: VirtualProtect failed");
                    return;
                }

                Marshal.Copy(patch, 0, _d3dPatchAddr, 6);
                VirtualProtect(_d3dPatchAddr, (UIntPtr)6, oldProtect, out _);
                Debug.WriteLine("[GameCubeHandler] D3D11CreateDevice patched to return E_FAIL");
            }
            catch (Exception ex) { Debug.WriteLine($"[GameCubeHandler] BlockD3D11 error: {ex.Message}"); }
        }

        private static void UnblockD3D11()
        {
            try
            {
                if (_d3dOrigBytes == null || _d3dPatchAddr == IntPtr.Zero) return;
                if (!VirtualProtect(_d3dPatchAddr, (UIntPtr)6, 0x40, out uint oldProtect)) return;
                Marshal.Copy(_d3dOrigBytes, 0, _d3dPatchAddr, 6);
                VirtualProtect(_d3dPatchAddr, (UIntPtr)6, oldProtect, out _);
                _d3dOrigBytes = null;
                Debug.WriteLine("[GameCubeHandler] D3D11CreateDevice restored");
            }
            catch (Exception ex) { Debug.WriteLine($"[GameCubeHandler] UnblockD3D11 error: {ex.Message}"); }
        }
    }
}
