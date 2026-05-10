using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Emutastic.Services
{
    /// <summary>
    /// One-shot Windows.Graphics.Capture grab of a single HWND's frame to a
    /// BGRA32 BitmapSource. Used for save-state screenshots when the core
    /// renders to a native Vulkan/GL overlay window — RenderTargetBitmap and
    /// PrintWindow both fail to capture compositor-level surfaces, but WGC
    /// captures the same image DWM puts on screen.
    /// </summary>
    public static class WgcSnapshotService
    {
        public static bool IsSupported
        {
            get
            {
                try { return GraphicsCaptureSession.IsSupported(); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Capture one frame from <paramref name="hwnd"/>. Blocks the calling
        /// thread up to <paramref name="timeoutMs"/> waiting for the first
        /// FrameArrived event. Returns null on failure (unsupported OS, zero
        /// size window, timeout, marshaling error).
        /// </summary>
        public static BitmapSource? Capture(IntPtr hwnd, int timeoutMs = 1000)
        {
            if (hwnd == IntPtr.Zero) return null;
            if (!IsSupported) return null;

            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            IDirect3DDevice? winrtDevice = null;
            GraphicsCaptureItem? captureItem = null;
            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession? session = null;
            ID3D11Texture2D? staging = null;
            using var frameEvent = new ManualResetEventSlim(false);
            void OnFrame(Direct3D11CaptureFramePool s, object e) => frameEvent.Set();

            try
            {
                // 1. D3D11 device with BGRA + the WinRT wrapper required by WGC.
                if (D3D11.D3D11CreateDevice(
                        adapter: null!,
                        DriverType.Hardware,
                        DeviceCreationFlags.BgraSupport,
                        null!,
                        out device,
                        out context).Failure)
                    return null;

                using (var dxgiDevice = device!.QueryInterface<IDXGIDevice>())
                {
                    int hr = CreateDirect3D11DeviceFromDXGIDevice(
                        dxgiDevice.NativePointer, out IntPtr winrtPtr);
                    if (hr != 0) return null;
                    try
                    {
                        winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(winrtPtr);
                    }
                    finally { Marshal.Release(winrtPtr); }
                }

                // 2. GraphicsCaptureItem from the HWND via the interop factory.
                captureItem = CreateCaptureItemForWindow(hwnd);
                if (captureItem == null) return null;
                int w = captureItem.Size.Width, h = captureItem.Size.Height;
                if (w <= 0 || h <= 0) return null;

                // 3. Frame pool + capture session.
                framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDevice!,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    captureItem.Size);
                framePool.FrameArrived += OnFrame;

                session = framePool.CreateCaptureSession(captureItem);
                try { session.IsBorderRequired = false; } catch { }
                try { session.IsCursorCaptureEnabled = false; } catch { }
                session.StartCapture();

                // 4. Drain frames for a short window (multiple frames after the
                //    first FrameArrived) and use the most recent. WGC's first
                //    captured frame is often the cleared compositor backbuffer
                //    before the core has presented its content — taking it
                //    yields a black image. By waiting through 4-5 frame cycles
                //    we land on a frame that reflects the current overlay state.
                if (!frameEvent.Wait(timeoutMs))
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[WgcSnapshot] Timeout after {timeoutMs}ms");
                    return null;
                }

                Direct3D11CaptureFrame? latest = null;
                int drained = 0;
                var drainStart = Environment.TickCount;
                while (Environment.TickCount - drainStart < 250)
                {
                    var f = framePool.TryGetNextFrame();
                    if (f != null)
                    {
                        latest?.Dispose();
                        latest = f;
                        drained++;
                    }
                    if (!frameEvent.Wait(60)) break;
                }

                if (latest == null) return null;
                using var frame = latest;
                System.Diagnostics.Trace.WriteLine(
                    $"[WgcSnapshot] Drained {drained} frames before capture");

                // 5. Unwrap WGC surface to ID3D11Texture2D so we can copy to a
                //    CPU-readable staging texture.
                using var srcTex = GetD3D11Texture(frame.Surface, device!);
                if (srcTex == null) return null;

                var desc = srcTex.Description;
                int srcW = (int)desc.Width;
                int srcH = (int)desc.Height;
                desc.Usage          = ResourceUsage.Staging;
                desc.BindFlags      = BindFlags.None;
                desc.CPUAccessFlags = CpuAccessFlags.Read;
                desc.MiscFlags      = ResourceOptionFlags.None;
                staging = device!.CreateTexture2D(desc);
                context!.CopyResource(staging, srcTex);

                // 6. Map staging, copy row-by-row to a tightly-packed BGRA buffer.
                var mapped = context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                int dstStride = srcW * 4;
                byte[] pixels = new byte[dstStride * srcH];
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    fixed (byte* dst = pixels)
                    {
                        for (int y = 0; y < srcH; y++)
                        {
                            Buffer.MemoryCopy(
                                source:           src + (long)y * mapped.RowPitch,
                                destination:      dst + (long)y * dstStride,
                                destinationSizeInBytes: dstStride,
                                sourceBytesToCopy:      dstStride);
                        }
                    }
                }
                context.Unmap(staging, 0);

                // Sanity probe: sample a few pixels so the log can distinguish
                // "captured a real frame" from "captured the cleared backbuffer".
                long sum = 0;
                int samples = 0;
                int step = Math.Max(1, (srcW * srcH) / 200);
                for (int i = 0; i < pixels.Length; i += step * 4)
                {
                    sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
                    samples++;
                }
                int avg = samples > 0 ? (int)(sum / (samples * 3)) : 0;
                System.Diagnostics.Trace.WriteLine(
                    $"[WgcSnapshot] {srcW}x{srcH} avg-luma={avg}/255");

                var bmp = BitmapSource.Create(srcW, srcH, 96, 96,
                    PixelFormats.Bgra32, null, pixels, dstStride);
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[WgcSnapshot] Failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (framePool != null)
                {
                    try { framePool.FrameArrived -= OnFrame; } catch { }
                }
                staging?.Dispose();
                session?.Dispose();
                framePool?.Dispose();
                context?.Dispose();
                device?.Dispose();
            }
        }

        // ── Win32 / WinRT interop ──────────────────────────────────────────

        [DllImport("d3d11.dll", PreserveSig = false)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoGetActivationFactory(
            IntPtr classId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern void WindowsDeleteString(IntPtr hstring);

        // IGraphicsCaptureItemInterop::CreateForWindow (vtable slot 3 after IUnknown)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateForWindowDelegate(
            IntPtr pThis, IntPtr window, ref Guid iid, out IntPtr result);

        private static readonly Guid IID_IGraphicsCaptureItem =
            new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid IID_IGraphicsCaptureItemInterop =
            new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

        private static GraphicsCaptureItem? CreateCaptureItemForWindow(IntPtr hwnd)
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(className, className.Length, out IntPtr hClassName);
            try
            {
                Guid interopIid = IID_IGraphicsCaptureItemInterop;
                RoGetActivationFactory(hClassName, ref interopIid, out IntPtr interopPtr);

                IntPtr vtable = Marshal.ReadIntPtr(interopPtr);
                IntPtr createSlot = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                var createForWindow =
                    Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(createSlot);

                Guid itemIid = IID_IGraphicsCaptureItem;
                int hr = createForWindow(interopPtr, hwnd, ref itemIid, out IntPtr itemPtr);
                Marshal.Release(interopPtr);
                Marshal.ThrowExceptionForHR(hr);

                var item = GraphicsCaptureItem.FromAbi(itemPtr);
                Marshal.Release(itemPtr);
                return item;
            }
            finally { WindowsDeleteString(hClassName); }
        }

        // IDirect3DDxgiInterfaceAccess is a raw COM interface — NOT a WinRT
        // projection — so we can't use surface.As<T>() to QI it. Get the
        // surface's IUnknown via marshaling, do a raw QueryInterface for the
        // access IID, then call its single method (GetInterface) through the
        // vtable. Same pattern this codebase already uses for
        // IGraphicsCaptureItemInterop.
        private static readonly Guid IID_IDirect3DDxgiInterfaceAccess =
            new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private static readonly Guid IID_ID3D11Texture2D =
            new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInterfaceDelegate(
            IntPtr pThis, ref Guid iid, out IntPtr ppv);

        private static ID3D11Texture2D? GetD3D11Texture(IDirect3DSurface surface, ID3D11Device device)
        {
            IntPtr unknownPtr = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
            if (unknownPtr == IntPtr.Zero) return null;

            IntPtr accessPtr = IntPtr.Zero;
            try
            {
                Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
                int hr = Marshal.QueryInterface(unknownPtr, ref accessIid, out accessPtr);
                if (hr != 0 || accessPtr == IntPtr.Zero)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[WgcSnapshot] QI IDirect3DDxgiInterfaceAccess hr=0x{hr:X}");
                    return null;
                }

                // vtable: [0]=QI, [1]=AddRef, [2]=Release, [3]=GetInterface
                IntPtr vtable = Marshal.ReadIntPtr(accessPtr);
                IntPtr getInterfaceFn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                var getInterface =
                    Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(getInterfaceFn);

                Guid texIid = IID_ID3D11Texture2D;
                int hr2 = getInterface(accessPtr, ref texIid, out IntPtr texPtr);
                if (hr2 != 0 || texPtr == IntPtr.Zero)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[WgcSnapshot] GetInterface(ID3D11Texture2D) hr=0x{hr2:X}");
                    return null;
                }
                return new ID3D11Texture2D(texPtr);
            }
            finally
            {
                if (accessPtr != IntPtr.Zero) Marshal.Release(accessPtr);
                Marshal.Release(unknownPtr);
            }
        }
    }
}
