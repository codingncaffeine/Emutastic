using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using D9 = Vortice.Direct3D9;

namespace Emutastic.Services
{
    /// <summary>
    /// Frontend-owned Direct3D 11 device for the libretro D3D11 HW-render path
    /// (RETRO_HW_CONTEXT_D3D11), driving LRPS2's D3D11 GS backend — the renderer
    /// proven full-speed on this rig.
    ///
    /// Two jobs:
    ///   1. Create the device + immediate context, hand them to the core via the
    ///      retro_hw_render_interface_d3d11 (GET_HW_RENDER_INTERFACE).
    ///   2. Each frame, pull the core's output texture off PS-SRV-slot-0 (where it
    ///      leaves it before video_refresh — verified from RetroArch's d3d11
    ///      driver), copy it into a SHARED texture, and expose that as a D3D9Ex
    ///      surface a WPF D3DImage can display in-tree (no overlay window).
    /// </summary>
    public sealed class D3D11Context : IDisposable
    {
        private const int  RETRO_HW_RENDER_INTERFACE_D3D11 = 3;
        private const uint RETRO_HW_RENDER_INTERFACE_D3D11_VERSION = 1;

        private ID3D11Device?        _device;
        private ID3D11DeviceContext? _context;
        private FeatureLevel         _featureLevel;

        private IntPtr _d3dCompilePtr;
        private IntPtr _d3dCompilerLib;
        private IntPtr _ifaceStruct;

        // ── Present bridge resources ────────────────────────────────────────
        private ID3D11Texture2D?       _sharedTex;     // D3D11 side of the shared surface (BGRA)
        private ID3D11RenderTargetView? _sharedRtv;     // RTV for the converting blit
        private ID3D11VertexShader?    _vs;
        private ID3D11PixelShader?     _ps;
        private ID3D11SamplerState?    _sampler;
        private D9.IDirect3D9Ex?       _d9;
        private D9.IDirect3DDevice9Ex? _d9Device;
        private D9.IDirect3DTexture9?  _d9Tex;          // D3D9 view of the same surface
        private D9.IDirect3DSurface9?  _d9Surface;
        private int _presentW, _presentH;

        // ── Swapchain present (preferred path; bypasses the D3D9/D3DImage copy) ──
        // Presents the core's frame straight to a DXGI swapchain on a child HWND,
        // downsampling into a display-sized backbuffer — GPU→GPU, vsync-paced, no
        // CPU-visible surface. See project_ps2_d3d11_present_scaling.
        private IDXGISwapChain1?        _swapChain;
        private ID3D11RenderTargetView? _backbufferRtv;
        private int _scW, _scH;
        public bool HasSwapchain => _swapChain != null;

        /// <summary>D3D9 surface backing the WPF D3DImage (set after EnsurePresentTarget).</summary>
        public IntPtr D9SurfacePointer => _d9Surface?.NativePointer ?? IntPtr.Zero;

        public bool Initialize()
        {
            try
            {
                D3D11.D3D11CreateDevice(
                    null, DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    Array.Empty<FeatureLevel>(),
                    out _device, out _context).CheckError();

                _featureLevel = _device!.FeatureLevel;

                _d3dCompilerLib = NativeLibrary.Load("d3dcompiler_47.dll");
                _d3dCompilePtr  = NativeLibrary.GetExport(_d3dCompilerLib, "D3DCompile");

                System.Diagnostics.Trace.WriteLine(
                    $"[D3D11] Device created, featureLevel=0x{(int)_featureLevel:X}, D3DCompile={_d3dCompilePtr:X}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[D3D11] Initialize failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// retro_hw_render_interface_d3d11 the core reads on GET_HW_RENDER_INTERFACE.
        /// x64 layout: 0 type / 4 version / 8 handle / 16 device / 24 context /
        /// 32 featureLevel / 40 D3DCompile.
        /// </summary>
        public IntPtr BuildHwRenderInterface()
        {
            if (_device == null || _context == null) return IntPtr.Zero;
            if (_ifaceStruct == IntPtr.Zero) _ifaceStruct = Marshal.AllocHGlobal(48);

            Marshal.WriteInt32 (_ifaceStruct, 0,  RETRO_HW_RENDER_INTERFACE_D3D11);
            Marshal.WriteInt32 (_ifaceStruct, 4,  (int)RETRO_HW_RENDER_INTERFACE_D3D11_VERSION);
            Marshal.WriteIntPtr(_ifaceStruct, 8,  IntPtr.Zero);
            Marshal.WriteIntPtr(_ifaceStruct, 16, _device.NativePointer);
            Marshal.WriteIntPtr(_ifaceStruct, 24, _context.NativePointer);
            Marshal.WriteInt32 (_ifaceStruct, 32, (int)_featureLevel);
            Marshal.WriteIntPtr(_ifaceStruct, 40, _d3dCompilePtr);
            return _ifaceStruct;
        }

        /// <summary>
        /// (Re)creates the shared D3D11 texture and its D3D9 view at w×h. Returns
        /// true when the surface was (re)created — the caller must then rebind the
        /// D3DImage back buffer to the new <see cref="D9SurfacePointer"/>. No-op
        /// (returns false) when the size is unchanged. Call on the UI thread (it
        /// touches the D3D9 device which is created against the WPF HWND).
        /// </summary>
        public bool EnsurePresentTarget(int w, int h, IntPtr hwnd)
        {
            if (_device == null || w <= 0 || h <= 0) return false;
            if (_sharedTex != null && w == _presentW && h == _presentH) return false;

            ReleasePresentTarget();
            _presentW = w; _presentH = h;

            // The shared texture is BGRA — the ONLY format the D3D9 shared-open
            // accepts here (D3D11 B8G8R8A8 <-> D3D9 A8R8G8B8). The core renders
            // RGBA, so we can't CopyResource into this (channel-order mismatch =
            // silent no-op); instead a shader blit samples the core's RGBA texture
            // and writes into this BGRA target, letting the GPU do the swizzle.
            var desc = new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };
            _sharedTex = _device.CreateTexture2D(desc);
            _sharedRtv = _device.CreateRenderTargetView(_sharedTex);
            EnsureBlitShaders();

            using var dxgiRes = _sharedTex.QueryInterface<IDXGIResource>();
            IntPtr sharedHandle = dxgiRes.SharedHandle;

            if (_d9 == null)
            {
                _d9 = D9.D3D9.Direct3DCreate9Ex();
                var pp = new D9.PresentParameters
                {
                    Windowed = true,
                    SwapEffect = D9.SwapEffect.Discard,
                    DeviceWindowHandle = hwnd,
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                    BackBufferFormat = D9.Format.Unknown,
                };
                _d9Device = _d9.CreateDeviceEx(0, D9.DeviceType.Hardware, hwnd,
                    D9.CreateFlags.HardwareVertexProcessing | D9.CreateFlags.Multithreaded | D9.CreateFlags.FpuPreserve,
                    pp);
            }

            // Open the D3D11 shared surface as a D3D9 texture (pass the handle IN).
            // A8R8G8B8 is the broadly-supported D3D9 shared-RT format (A8B8G8R8
            // is rejected as D3DERR_INVALIDCALL on this driver). Opening an RGBA
            // D3D11 surface as A8R8G8B8 may swap R/B; colors are corrected by a
            // shader blit once the pipeline is confirmed visible.
            IntPtr sh = sharedHandle;
            _d9Tex = _d9Device!.CreateTexture((uint)w, (uint)h, 1,
                D9.Usage.RenderTarget, D9.Format.A8R8G8B8, D9.Pool.Default, ref sh);   // BGRA, matches shared D3D11 tex
            _d9Surface = _d9Tex.GetSurfaceLevel(0);

            System.Diagnostics.Trace.WriteLine($"[D3D11] Present target {w}x{h}, D3D9 surface={_d9Surface.NativePointer:X}");
            return true;
        }

        /// <summary>
        /// Copies the core's current output (PS-SRV-slot-0 on our shared context)
        /// into the shared texture. Call on the emu thread inside the video_refresh
        /// handler, right after retro_run produced the frame. Returns false if no
        /// SRV was bound (nothing to show this frame).
        /// </summary>
        // Fullscreen-triangle blit: sample the core's RGBA output, write into the
        // BGRA shared target. The output-merger writes our sampled color into the
        // BGRA surface, so the GPU performs the RGBA→BGRA channel swizzle for free.
        private const string BlitHlsl = @"
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut VSMain(uint id : SV_VertexID) {
  VSOut o;
  o.uv  = float2((id << 1) & 2, id & 2);
  o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
  return o;
}
Texture2D tex : register(t0);
SamplerState smp : register(s0);
float4 PSMain(VSOut i) : SV_TARGET { return tex.Sample(smp, i.uv); }";

        private void EnsureBlitShaders()
        {
            if (_vs != null || _device == null) return;
            var vsb = Compiler.Compile(BlitHlsl, "VSMain", "blit.hlsl", "vs_4_0", ShaderFlags.None, EffectFlags.None);
            var psb = Compiler.Compile(BlitHlsl, "PSMain", "blit.hlsl", "ps_4_0", ShaderFlags.None, EffectFlags.None);
            _vs = _device.CreateVertexShader(vsb.Span.ToArray(), null);
            _ps = _device.CreatePixelShader(psb.Span.ToArray(), null);
            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MinLOD = 0, MaxLOD = float.MaxValue,
            });
            System.Diagnostics.Trace.WriteLine("[D3D11] blit shaders compiled");
        }

        private readonly ID3D11ShaderResourceView[] _srvScratch = new ID3D11ShaderResourceView[1];
        public bool CaptureCoreFrame()
        {
            if (_context == null || _sharedTex == null || _sharedRtv == null
                || _vs == null || _ps == null || _sampler == null) return false;
            _srvScratch[0] = null!;
            _context.PSGetShaderResources(0, 1, _srvScratch);
            var srv = _srvScratch[0];
            if (srv == null) return false;
            try
            {
                _context.OMSetRenderTargets(_sharedRtv, null);
                _context.RSSetViewport(0, 0, _presentW, _presentH, 0f, 1f);
                _context.VSSetShader(_vs);
                _context.PSSetShader(_ps);
                _context.PSSetSampler(0, _sampler);
                _context.PSSetShaderResource(0, srv);
                _context.IASetInputLayout(null);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.Draw(3, 0);
                _context.Flush();
                return true;
            }
            finally { srv.Dispose(); }
        }

        // ── Swapchain path ──────────────────────────────────────────────────
        /// <summary>
        /// Creates a DXGI swapchain on <paramref name="hwnd"/> (the child overlay
        /// window) sized w×h (display size). Present blits the core's high-res frame
        /// down into this backbuffer — no D3D9, no shared surface, no WPF copy.
        /// Call once after the overlay HWND exists. Returns false on failure (caller
        /// can fall back to the D3DImage path).
        /// </summary>
        public bool CreateSwapchain(IntPtr hwnd, int w, int h)
        {
            if (_device == null || hwnd == IntPtr.Zero || w <= 0 || h <= 0) return false;
            try
            {
                EnsureBlitShaders();
                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();
                var desc = new SwapChainDescription1
                {
                    Width = (uint)w, Height = (uint)h,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = SwapChainFlags.None,
                };
                _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, desc);
                factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
                CreateBackbufferRtv();
                _scW = w; _scH = h;
                System.Diagnostics.Trace.WriteLine($"[D3D11] swapchain created {w}x{h} on HWND=0x{hwnd:X}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[D3D11] CreateSwapchain failed: {ex}");
                _swapChain?.Dispose(); _swapChain = null;
                return false;
            }
        }

        private void CreateBackbufferRtv()
        {
            using var bb = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            _backbufferRtv = _device!.CreateRenderTargetView(bb);
        }

        private readonly ID3D11ShaderResourceView[] _srvScratchSc = new ID3D11ShaderResourceView[1];
        /// <summary>
        /// Blits the core's current output (PS-SRV-slot-0) into the swapchain
        /// backbuffer (downsampling to display size) and presents it at vsync. Call
        /// on the emu thread right after retro_run. Present(1,…) paces the emu thread
        /// to the refresh rate — built-in back-pressure, no UI-thread round trip.
        /// </summary>
        public bool PresentFrame()
        {
            if (_context == null || _swapChain == null || _backbufferRtv == null
                || _vs == null || _ps == null || _sampler == null) return false;
            ApplyPendingResize();   // emu thread — serialize resize with present
            _srvScratchSc[0] = null!;
            _context.PSGetShaderResources(0, 1, _srvScratchSc);
            var srv = _srvScratchSc[0];
            if (srv == null) return false;   // nothing rendered yet — hold last frame
            try
            {
                _context.OMSetRenderTargets(_backbufferRtv, null);
                _context.RSSetViewport(0, 0, _scW, _scH, 0f, 1f);
                _context.VSSetShader(_vs);
                _context.PSSetShader(_ps);
                _context.PSSetSampler(0, _sampler);
                _context.PSSetShaderResource(0, srv);
                _context.IASetInputLayout(null);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.Draw(3, 0);
                _swapChain.Present(1, PresentFlags.None);
                return true;
            }
            finally { srv.Dispose(); }
        }

        // UI thread requests a resize; the emu thread applies it inside PresentFrame.
        // DXGI forbids ResizeBuffers running concurrently with Present on the same
        // swapchain (it would block/deadlock — the cause of multi-second locks), so
        // the two MUST happen on the same thread.
        private volatile int _pendingScW, _pendingScH;

        /// <summary>Requests a backbuffer resize (display/overlay size changed). Safe
        /// to call from the UI thread — the actual ResizeBuffers is deferred to the
        /// emu thread's next PresentFrame.</summary>
        public void RecreateSwapchain(int w, int h)
        {
            if (_swapChain == null || w <= 0 || h <= 0) return;
            _pendingScW = w; _pendingScH = h;
        }

        private void ApplyPendingResize()
        {
            int w = _pendingScW, h = _pendingScH;
            if (w <= 0 || h <= 0) return;
            _pendingScW = 0; _pendingScH = 0;
            if (w == _scW && h == _scH) return;
            try
            {
                _backbufferRtv?.Dispose(); _backbufferRtv = null;
                _swapChain!.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                CreateBackbufferRtv();
                _scW = w; _scH = h;
                System.Diagnostics.Trace.WriteLine($"[D3D11] swapchain resized {w}x{h}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[D3D11] ResizeBuffers failed: {ex}");
            }
        }

        private void ReleaseSwapchain()
        {
            _backbufferRtv?.Dispose(); _backbufferRtv = null;
            _swapChain?.Dispose();     _swapChain = null;
        }

        private void ReleasePresentTarget()
        {
            _d9Surface?.Dispose(); _d9Surface = null;
            _d9Tex?.Dispose();     _d9Tex = null;
            _sharedRtv?.Dispose(); _sharedRtv = null;
            _sharedTex?.Dispose(); _sharedTex = null;
        }

        public void Dispose()
        {
            if (_ifaceStruct != IntPtr.Zero) { Marshal.FreeHGlobal(_ifaceStruct); _ifaceStruct = IntPtr.Zero; }
            ReleaseSwapchain();
            ReleasePresentTarget();
            _sampler?.Dispose(); _sampler = null;
            _ps?.Dispose();      _ps = null;
            _vs?.Dispose();      _vs = null;
            _d9Device?.Dispose(); _d9Device = null;
            _d9?.Dispose();       _d9 = null;
            _context?.Dispose();  _context = null;
            _device?.Dispose();   _device = null;
            if (_d3dCompilerLib != IntPtr.Zero) { NativeLibrary.Free(_d3dCompilerLib); _d3dCompilerLib = IntPtr.Zero; }
        }
    }
}
