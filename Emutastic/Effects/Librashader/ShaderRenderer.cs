using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Emutastic.Effects.Librashader;

/// <summary>
/// Runs a downloaded libretro <c>.slangp</c> multi-pass shader preset over a
/// software-rendered emulator frame, via librashader's D3D11 runtime, and reads
/// the result back to a CPU BGRA buffer for the existing WriteableBitmap path.
///
/// THREADING: owned entirely by the emulation thread (the same thread that calls
/// <see cref="OnVideoRefresh"/> in EmulatorWindow). The D3D11 immediate context
/// is single-threaded; only ever touch this from that thread. The caller marshals
/// the returned byte[] to the UI thread for the WriteableBitmap copy, so no GPU
/// work runs on the UI thread (see feedback: UI thread never blocks).
///
/// Phase 1 deliberately uses GPU->CPU readback into the existing WriteableBitmap
/// rather than a D3DImage shared surface: it keeps the HUD/AR/screenshot/recording
/// paths untouched. A D3DImage path is a later perf optimization if readback proves
/// too costly for heavy shaders.
/// </summary>
public sealed class ShaderRenderer : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IntPtr _chain;                 // libra_d3d11_filter_chain_t

    private ID3D11Texture2D? _inTex;
    private ID3D11ShaderResourceView? _inSrv;
    private int _inW, _inH;

    private ID3D11Texture2D? _outTex;
    private ID3D11RenderTargetView? _outRtv;
    private ID3D11Texture2D? _staging;
    private int _outW, _outH;

    private byte[] _outBuffer = Array.Empty<byte>();
    private byte[] _uploadScratch = Array.Empty<byte>();
    private ulong _frameCount;

    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Loads librashader (if needed), creates a D3D11 device, and builds the
    /// filter chain for <paramref name="presetPath"/>. Returns false on any
    /// failure — the caller then falls back to the built-in WPF shaders.
    /// Call on the emulation thread.
    /// </summary>
    public bool Initialize(string librashaderDllPath, string presetPath)
    {
        try
        {
            if (!LibrashaderInterop.Load(librashaderDllPath))
            {
                LastError = "librashader.dll not available";
                return false;
            }
            // ABI guard: librashader ABIs are NOT backwards-compatible. Our prebuilt
            // is ABI 2 (LIBRASHADER_CURRENT_ABI). A mismatched DLL must fail safe here
            // instead of corrupting arg/struct layout at the native frame call.
            if (LibrashaderInterop.AbiVersion() != 2)
            {
                LastError = "librashader ABI mismatch (need 2)";
                return false;
            }
            if (string.IsNullOrWhiteSpace(presetPath) || !System.IO.File.Exists(presetPath))
            {
                LastError = "preset not found";
                return false;
            }

            // D3D11 device. BgraSupport so B8G8R8A8 render targets are valid.
            if (D3D11.D3D11CreateDevice(
                    adapter: null!,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    null!,
                    out _device,
                    out _context).Failure)
            {
                LastError = "D3D11CreateDevice failed";
                return false;
            }

            // Parse preset, then build the chain (the create call consumes/invalidates
            // the preset handle — do not use or free it afterwards).
            IntPtr err = LibrashaderInterop.PresetCreate(presetPath, out IntPtr preset);
            int code = LibrashaderInterop.ConsumeError(err);
            if (code != 0 || preset == IntPtr.Zero)
            {
                LastError = $"preset_create failed (errno {code})";
                return false;
            }

            err = LibrashaderInterop.D3D11ChainCreate(ref preset, _device!.NativePointer, IntPtr.Zero, out _chain);
            code = LibrashaderInterop.ConsumeError(err);
            if (code != 0 || _chain == IntPtr.Zero)
            {
                LastError = $"filter_chain_create failed (errno {code})";
                return false;
            }

            IsReady = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Runs the shader chain over one frame and returns a tightly-packed BGRA32
    /// buffer at the shaded output resolution. Returns null on failure (caller
    /// falls back to the raw frame). Call on the emulation thread only.
    /// </summary>
    public byte[]? Process(byte[] frame, int width, int height, int srcPitch, bool isBgr32, out int outW, out int outH)
    {
        outW = 0; outH = 0;
        if (!IsReady || _device == null || _context == null || width <= 0 || height <= 0)
            return null;

        try
        {
            EnsureInput(width, height);
            EnsureOutput(width, height);

            UploadFrame(frame, width, height, srcPitch, isBgr32);

            var vp = new LibrashaderInterop.LibraViewport { X = 0, Y = 0, Width = (uint)_outW, Height = (uint)_outH };
            IntPtr err = LibrashaderInterop.D3D11ChainFrame(
                ref _chain, _context.NativePointer, (UIntPtr)_frameCount,
                _inSrv!.NativePointer, _outRtv!.NativePointer, ref vp,
                IntPtr.Zero, IntPtr.Zero);
            _frameCount++;

            int code = LibrashaderInterop.ConsumeError(err);
            if (code != 0) { LastError = $"filter_chain_frame errno {code}"; return null; }

            // Read back the shaded output.
            _context.CopyResource(_staging!, _outTex!);
            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int dstStride = _outW * 4;
                if (_outBuffer.Length != dstStride * _outH)
                    _outBuffer = new byte[dstStride * _outH];
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    fixed (byte* dst = _outBuffer)
                    {
                        for (int y = 0; y < _outH; y++)
                            Buffer.MemoryCopy(src + (long)y * mapped.RowPitch,
                                              dst + (long)y * dstStride,
                                              dstStride, dstStride);
                    }
                }
            }
            finally { _context.Unmap(_staging!, 0); }

            outW = _outW; outH = _outH;
            return _outBuffer;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private void EnsureInput(int w, int h)
    {
        if (_inTex != null && _inW == w && _inH == h) return;
        _inSrv?.Dispose(); _inTex?.Dispose();
        _inW = w; _inH = h;

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        };
        _inTex = _device!.CreateTexture2D(desc);
        _inSrv = _device.CreateShaderResourceView(_inTex);
    }

    private void EnsureOutput(int inW, int inH)
    {
        // Target ~720 lines so multi-pass CRT/scanline detail resolves, capped to
        // keep the per-frame readback bounded.
        int scale = Math.Clamp((int)Math.Round(720.0 / inH), 1, 4);
        int w = inW * scale, h = inH * scale;
        if (_outTex != null && _outW == w && _outH == h) return;
        _outRtv?.Dispose(); _outTex?.Dispose(); _staging?.Dispose();
        _outW = w; _outH = h;

        _outTex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None
        });
        _outRtv = _device.CreateRenderTargetView(_outTex);

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None
        });
    }

    /// <summary>
    /// Uploads the core frame into the dynamic input texture as BGRA8888.
    /// Source rows are <paramref name="srcPitch"/> bytes apart — the core's pitch
    /// frequently exceeds width*bpp (padding), e.g. Genesis at 320px. Assuming a
    /// packed w*bpp stride skews/tears the image on those cores.
    /// </summary>
    private void UploadFrame(byte[] frame, int w, int h, int srcPitch, bool isBgr32)
    {
        var mapped = _context!.Map(_inTex!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int dstStride = (int)mapped.RowPitch;
            unsafe
            {
                byte* dstBase = (byte*)mapped.DataPointer;
                if (isBgr32)
                {
                    int rowBytes = w * 4;
                    fixed (byte* src = frame)
                    {
                        for (int y = 0; y < h; y++)
                            Buffer.MemoryCopy(src + (long)y * srcPitch,
                                              dstBase + (long)y * dstStride,
                                              dstStride, Math.Min(rowBytes, dstStride));
                    }
                }
                else
                {
                    // RGB565 little-endian -> BGRA8888.
                    fixed (byte* src = frame)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ushort* srow = (ushort*)(src + (long)y * srcPitch);
                            byte* drow = dstBase + (long)y * dstStride;
                            for (int x = 0; x < w; x++)
                            {
                                ushort p = srow[x];
                                int r5 = (p >> 11) & 0x1F, g6 = (p >> 5) & 0x3F, b5 = p & 0x1F;
                                int o = x * 4;
                                drow[o + 0] = (byte)((b5 * 255 + 15) / 31); // B
                                drow[o + 1] = (byte)((g6 * 255 + 31) / 63); // G
                                drow[o + 2] = (byte)((r5 * 255 + 15) / 31); // R
                                drow[o + 3] = 255;                          // A
                            }
                        }
                    }
                }
            }
        }
        finally { _context!.Unmap(_inTex!, 0); }
    }

    public void Dispose()
    {
        try
        {
            if (_chain != IntPtr.Zero && LibrashaderInterop.Loaded)
            {
                IntPtr c = _chain; _chain = IntPtr.Zero;
                var err = LibrashaderInterop.D3D11ChainFree(ref c);
                LibrashaderInterop.ConsumeError(err);
            }
        }
        catch { /* best effort */ }

        _inSrv?.Dispose(); _inTex?.Dispose();
        _outRtv?.Dispose(); _outTex?.Dispose(); _staging?.Dispose();
        _context?.Dispose(); _device?.Dispose();
        _inSrv = null; _inTex = null; _outRtv = null; _outTex = null; _staging = null;
        _context = null; _device = null;
        IsReady = false;
    }
}
