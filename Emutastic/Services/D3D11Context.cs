using System;
using System.Runtime.InteropServices;

namespace Emutastic.Services
{
    /// <summary>
    /// Frontend-owned Direct3D 11 device for the libretro D3D11 hardware-render
    /// path (RETRO_HW_CONTEXT_D3D11 = 7), used by LRPS2's D3D11 GS backend — the
    /// renderer proven full-speed on this rig.
    ///
    /// Mirrors the role of <see cref="VulkanContext"/>: we create the device, hand
    /// it to the core through the libretro D3D11 render interface, and (later
    /// milestone) pull the core's rendered texture off PS-SRV-slot-0 each frame
    /// for presentation via a WPF D3DImage.
    ///
    /// Contract verified against libretro_d3d11.h + LRPS2 GSDevice11.cpp:
    ///   - core requests context_type D3D11, then calls GET_HW_RENDER_INTERFACE
    ///   - we return a retro_hw_render_interface_d3d11 with device/context/
    ///     featureLevel/D3DCompile
    ///   - core QueryInterfaces the device for ID3D11Device1, so we must create an
    ///     11.1-capable device (guaranteed on Win10+; this rig is Win11).
    /// </summary>
    public sealed class D3D11Context : IDisposable
    {
        // retro_hw_render_interface_type
        private const int  RETRO_HW_RENDER_INTERFACE_D3D11 = 3;
        private const uint RETRO_HW_RENDER_INTERFACE_D3D11_VERSION = 1;

        // D3D11CreateDevice args
        private const int  D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        private const uint D3D11_SDK_VERSION = 7;

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter, int driverType, IntPtr software, uint flags,
            IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        public IntPtr Device  { get; private set; }   // ID3D11Device*
        public IntPtr Context { get; private set; }   // ID3D11DeviceContext* (immediate)
        public int    FeatureLevel { get; private set; }

        private IntPtr _d3dCompilePtr;       // pD3DCompile from d3dcompiler_47.dll
        private IntPtr _d3dCompilerLib;       // module handle, freed on dispose
        private IntPtr _ifaceStruct;          // marshaled retro_hw_render_interface_d3d11

        /// <summary>
        /// Creates the device + immediate context and resolves the D3DCompile
        /// pointer. Returns false (and logs) on any failure so the caller can
        /// fall back rather than crash. Called once, off SET_HW_RENDER.
        /// </summary>
        public bool Initialize()
        {
            try
            {
                int hr = D3D11CreateDevice(
                    IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero, 0, D3D11_SDK_VERSION,
                    out IntPtr device, out int featureLevel, out IntPtr context);

                if (hr < 0 || device == IntPtr.Zero || context == IntPtr.Zero)
                {
                    System.Diagnostics.Trace.WriteLine($"[D3D11] D3D11CreateDevice failed hr=0x{hr:X8}");
                    return false;
                }

                Device = device;
                Context = context;
                FeatureLevel = featureLevel;

                // The core needs the D3DCompile entry point to build its shaders.
                // d3dcompiler_47.dll ships with Windows 10+/the runtime.
                _d3dCompilerLib = NativeLibrary.Load("d3dcompiler_47.dll");
                _d3dCompilePtr  = NativeLibrary.GetExport(_d3dCompilerLib, "D3DCompile");

                System.Diagnostics.Trace.WriteLine(
                    $"[D3D11] Device created, featureLevel=0x{featureLevel:X}, D3DCompile={_d3dCompilePtr:X}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[D3D11] Initialize failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Allocates and fills the retro_hw_render_interface_d3d11 the core reads
        /// on GET_HW_RENDER_INTERFACE, returning a pointer the env callback writes
        /// back. Owned here; freed on Dispose. x64 layout (8-byte aligned):
        ///   0  interface_type (int)      16 device  (ptr)   32 featureLevel (int)
        ///   4  interface_version (uint)  24 context (ptr)   40 D3DCompile   (ptr)
        ///   8  handle (ptr)                               [36 pad]
        /// </summary>
        public IntPtr BuildHwRenderInterface()
        {
            if (Device == IntPtr.Zero) return IntPtr.Zero;
            if (_ifaceStruct == IntPtr.Zero)
                _ifaceStruct = Marshal.AllocHGlobal(48);

            Marshal.WriteInt32(_ifaceStruct, 0,  RETRO_HW_RENDER_INTERFACE_D3D11);
            Marshal.WriteInt32(_ifaceStruct, 4,  (int)RETRO_HW_RENDER_INTERFACE_D3D11_VERSION);
            Marshal.WriteIntPtr(_ifaceStruct, 8,  IntPtr.Zero);      // handle (unused by core)
            Marshal.WriteIntPtr(_ifaceStruct, 16, Device);
            Marshal.WriteIntPtr(_ifaceStruct, 24, Context);
            Marshal.WriteInt32(_ifaceStruct, 32, FeatureLevel);
            Marshal.WriteIntPtr(_ifaceStruct, 40, _d3dCompilePtr);
            return _ifaceStruct;
        }

        public void Dispose()
        {
            if (_ifaceStruct != IntPtr.Zero) { Marshal.FreeHGlobal(_ifaceStruct); _ifaceStruct = IntPtr.Zero; }
            // Release our COM refs (the core holds its own from QueryInterface).
            if (Context != IntPtr.Zero) { Marshal.Release(Context); Context = IntPtr.Zero; }
            if (Device  != IntPtr.Zero) { Marshal.Release(Device);  Device  = IntPtr.Zero; }
            if (_d3dCompilerLib != IntPtr.Zero) { NativeLibrary.Free(_d3dCompilerLib); _d3dCompilerLib = IntPtr.Zero; }
        }
    }
}
