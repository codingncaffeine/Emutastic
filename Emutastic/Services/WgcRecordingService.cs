using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Security.Cryptography;
using Windows.Storage;
using WinRT;

namespace Emutastic.Services
{
    /// <summary>
    /// Records a native HWND using Windows.Graphics.Capture (compositor-level capture)
    /// and encodes to MP4 via MediaTranscoder (handles BGRA→H.264 conversion internally).
    /// </summary>
    public class WgcRecordingService : IRecordingService
    {
        // D3D11 device (our own, separate from the core's GL/Vulkan context)
        private ID3D11Device? _d3dDevice;
        private ID3D11DeviceContext? _d3dContext;
        private IDirect3DDevice? _winrtDevice; // WinRT wrapper for WGC

        // WGC capture objects
        private GraphicsCaptureItem? _captureItem;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _captureSession;

        // MediaStreamSource + MediaTranscoder (replaces MF SinkWriter)
        private MediaStreamSource? _mediaStreamSource;
        private VideoStreamDescriptor? _videoDesc;
        private AudioStreamDescriptor? _audioDesc;
        private TaskCompletionSource? _transcodeComplete;

        // Frame synchronization: FrameArrived signals, SampleRequested waits
        private readonly AutoResetEvent _frameEvent = new(false);

        // Audio queue (same pattern as RecordingService)
        private BlockingCollection<(byte[] buf, int len, bool rented)>? _audioQueue;

        // State
        private volatile bool _isRecording;
        private volatile bool _stopping;
        private int _completeFired; // Interlocked guard for single onComplete call
        private readonly object _lock = new();
        private DateTime _startTime;
        private readonly Stopwatch _stopwatch = new();
        private int _width, _height, _fps, _sampleRate;
        private long _audioTimestamp;
        private long _frameDuration;    // 100ns per frame
        private string _outputPath = "";
        private Action<string>? _onComplete;

        public bool IsRecording => _isRecording;
        public bool IsEncoding => false;
        public TimeSpan Elapsed => _isRecording ? DateTime.Now - _startTime : TimeSpan.Zero;

        private static void Log(string msg)
        {
            try
            {
                string logDir = @"D:\Emutastic Data\Logs";
                System.IO.Directory.CreateDirectory(logDir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(logDir, "recording_debug.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [WGC] {msg}\n");
            }
            catch { }
        }

        /// <summary>
        /// Returns true if Windows.Graphics.Capture is available on this OS version.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                try { return GraphicsCaptureSession.IsSupported(); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Start recording the given HWND. Returns null on success or error message.
        /// </summary>
        public string? Start(string outputPath, IntPtr hwnd, int fps, int sampleRate,
            Action<string>? onComplete = null)
        {
            lock (_lock)
            {
                if (_isRecording) return "Already recording";

                _outputPath = outputPath;
                _fps = fps > 0 ? fps : 60;
                _sampleRate = sampleRate;
                _stopping = false;
                _completeFired = 0;
                _onComplete = onComplete;
                _audioTimestamp = 0;
                _frameDuration = 10_000_000L / _fps; // 100ns units

                string? dir = System.IO.Path.GetDirectoryName(outputPath);
                if (dir != null) System.IO.Directory.CreateDirectory(dir);

                try
                {
                    // 1. Create D3D11 device
                    Log("Step 1: Creating D3D11 device...");
                    InitD3D11();
                    Log("Step 1: OK");

                    // 2. Create WGC capture from HWND
                    Log("Step 2: Creating WGC capture...");
                    InitCapture(hwnd);
                    if (_captureItem == null) return "Failed to create capture item for window";
                    // H.264 requires even dimensions
                    _width = _captureItem.Size.Width & ~1;
                    _height = _captureItem.Size.Height & ~1;
                    Log($"Step 2: OK — {_captureItem.Size.Width}x{_captureItem.Size.Height} → {_width}x{_height}");
                    if (_width <= 0 || _height <= 0) return "Window has zero size";

                    // 3. Create MediaStreamSource (BGRA8 video input + PCM audio input)
                    Log("Step 3: Creating MediaStreamSource...");
                    InitMediaStreamSource();
                    Log("Step 3: OK");

                    // 4. Audio queue
                    if (_sampleRate > 0)
                        _audioQueue = new BlockingCollection<(byte[], int, bool)>(boundedCapacity: 500);

                    // 5. Start capture — FrameArrived signals the AutoResetEvent
                    _framePool!.FrameArrived += OnFrameArrived;
                    _captureSession!.StartCapture();

                    _startTime = DateTime.Now;
                    _stopwatch.Restart();
                    _isRecording = true;

                    // 6. Fire off the MediaTranscoder on a background task
                    _transcodeComplete = new TaskCompletionSource();
                    _ = RunTranscodeAsync();

                    Log($"Started: {_width}x{_height}@{_fps}fps, HWND=0x{hwnd:X}");
                    return null;
                }
                catch (Exception ex)
                {
                    Log($"Start failed: {ex}");
                    CleanupAll();
                    return ex.Message;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRecording || _stopping) return;
                _stopping = true;
                _isRecording = false;
                _stopwatch.Stop();

                var elapsed = DateTime.Now - _startTime;
                Log($"Stopping after {elapsed:mm\\:ss}...");

                // Signal SampleRequested handlers to return null → end of stream
                // Non-blocking: RunTranscodeAsync handles finalization + cleanup in background
                _frameEvent.Set();
                _audioQueue?.CompleteAdding();
            }
        }

        public void QueueAudioSamples(byte[] sourceSamples, int length)
        {
            var q = _audioQueue;
            if (!_isRecording || q == null || q.IsAddingCompleted) return;

            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(sourceSamples, 0, rented, 0, length);

            try
            {
                if (!q.TryAdd((rented, length, true)))
                    ArrayPool<byte>.Shared.Return(rented);
            }
            catch (InvalidOperationException)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // ── D3D11 init ──────────────────────────────────────────────────────

        private void InitD3D11()
        {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                Array.Empty<Vortice.Direct3D.FeatureLevel>(),
                out _d3dDevice,
                out _d3dContext).CheckError();

            // Create WinRT IDirect3DDevice wrapper for WGC
            using var dxgiDevice = _d3dDevice!.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            _winrtDevice = CreateWinRTDevice(dxgiDevice);
        }

        // ── WGC capture init ────────────────────────────────────────────────

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = false)]
        private static extern void WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern void WindowsDeleteString(IntPtr hstring);

        // Raw vtable delegate for IGraphicsCaptureItemInterop::CreateForWindow
        // IGraphicsCaptureItemInterop inherits IUnknown: QI(0), AddRef(1), Release(2), CreateForWindow(3)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateForWindowDelegate(
            IntPtr pThis, IntPtr window, ref Guid iid, out IntPtr result);

        // IGraphicsCaptureItem IID — the interface, not the runtime class
        private static readonly Guid IID_IGraphicsCaptureItem =
            new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        // IGraphicsCaptureItemInterop IID
        private static readonly Guid IID_IGraphicsCaptureItemInterop =
            new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

        private void InitCapture(IntPtr hwnd)
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(className, className.Length, out IntPtr hClassName);
            try
            {
                // Get the activation factory, QI'd directly for IGraphicsCaptureItemInterop
                Guid interopIid = IID_IGraphicsCaptureItemInterop;
                RoGetActivationFactory(hClassName, ref interopIid, out IntPtr interopPtr);

                // Call CreateForWindow via raw vtable (slot 3 after IUnknown)
                IntPtr vtable = Marshal.ReadIntPtr(interopPtr);
                IntPtr createSlot = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                var createForWindow = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(createSlot);

                Guid itemIid = IID_IGraphicsCaptureItem;
                int hr = createForWindow(interopPtr, hwnd, ref itemIid, out IntPtr itemPtr);
                Marshal.Release(interopPtr);
                Marshal.ThrowExceptionForHR(hr);

                _captureItem = GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                WindowsDeleteString(hClassName);
            }

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2, // buffer count
                _captureItem.Size);

            _captureSession = _framePool.CreateCaptureSession(_captureItem);

            // Disable yellow border on Windows 11 22H2+
            try { _captureSession.IsBorderRequired = false; }
            catch { /* older OS — border will show */ }

            // Don't show cursor in recording
            try { _captureSession.IsCursorCaptureEnabled = false; }
            catch { }
        }

        // ── MediaStreamSource + MediaTranscoder ─────────────────────────────

        private void InitMediaStreamSource()
        {
            // Video input: BGRA8 (matches WGC output format)
            // The MediaTranscoder handles BGRA→YUV conversion internally with HW acceleration
            var videoProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)_width, (uint)_height);
            videoProps.FrameRate.Numerator = (uint)_fps;
            videoProps.FrameRate.Denominator = 1;
            _videoDesc = new VideoStreamDescriptor(videoProps);

            if (_sampleRate > 0)
            {
                var audioProps = AudioEncodingProperties.CreatePcm((uint)_sampleRate, 2, 16);
                _audioDesc = new AudioStreamDescriptor(audioProps);
                _mediaStreamSource = new MediaStreamSource(_videoDesc, _audioDesc);
            }
            else
            {
                _mediaStreamSource = new MediaStreamSource(_videoDesc);
            }

            _mediaStreamSource.BufferTime = TimeSpan.Zero;
            _mediaStreamSource.SampleRequested += OnSampleRequested;
        }

        private async Task RunTranscodeAsync()
        {
            try
            {
                var transcoder = new MediaTranscoder();
                transcoder.HardwareAccelerationEnabled = true;

                // Output profile: must be built explicitly — VideoEncodingQuality.Auto
                // doesn't work with MediaStreamSource inputs (MF_E_TRANSFORM_TYPE_NOT_SET)
                var profile = new MediaEncodingProfile();
                profile.Container = new ContainerEncodingProperties();
                profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
                profile.Video = new VideoEncodingProperties();
                profile.Video.Subtype = MediaEncodingSubtypes.H264;
                profile.Video.Width = (uint)_width;
                profile.Video.Height = (uint)_height;
                profile.Video.Bitrate = (uint)GetBitrate(_width, _height);
                profile.Video.FrameRate.Numerator = (uint)_fps;
                profile.Video.FrameRate.Denominator = 1;
                profile.Video.PixelAspectRatio.Numerator = 1;
                profile.Video.PixelAspectRatio.Denominator = 1;

                if (_sampleRate > 0)
                {
                    profile.Audio = AudioEncodingProperties.CreateAac(
                        (uint)_sampleRate, 2, 192000);
                }
                else
                {
                    profile.Audio = null;
                }

                // Open output file via WinRT StorageFile API
                string dir = System.IO.Path.GetDirectoryName(_outputPath)!;
                string filename = System.IO.Path.GetFileName(_outputPath);
                var folder = await StorageFolder.GetFolderFromPathAsync(dir);
                var file = await folder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);

                Log($"Opening output: {_outputPath}");
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);

                var prepResult = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                    _mediaStreamSource!, stream, profile);

                if (!prepResult.CanTranscode)
                {
                    Log($"Cannot transcode: {prepResult.FailureReason}");
                    _isRecording = false;
                    if (Interlocked.CompareExchange(ref _completeFired, 1, 0) == 0)
                        _onComplete?.Invoke($"Recording failed: cannot transcode ({prepResult.FailureReason})");
                    return;
                }

                Log("Transcoding started...");
                await prepResult.TranscodeAsync();
                Log("Transcode complete");
            }
            catch (Exception ex)
            {
                // If we're stopping, this is expected (end-of-stream triggers cancellation)
                if (!_stopping)
                {
                    Log($"Transcode error: {ex}");
                    _isRecording = false;
                    if (Interlocked.CompareExchange(ref _completeFired, 1, 0) == 0)
                        _onComplete?.Invoke($"Recording failed: {ex.Message}");
                }
                else
                {
                    Log($"Transcode ended on stop: {ex.Message}");
                }
            }
            finally
            {
                // Notify completion and clean up resources in background
                // (Stop() is non-blocking — it just signals, we handle the rest here)
                if (_stopping && Interlocked.CompareExchange(ref _completeFired, 1, 0) == 0)
                {
                    Log($"MP4 saved: {_outputPath}");
                    _onComplete?.Invoke(_outputPath);
                }

                CleanupAll();
                _transcodeComplete?.TrySetResult();
            }
        }

        // ── WGC frame callback (thread pool thread) ────────────────────────

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_isRecording)
                _frameEvent.Set();
        }

        // ── Sample pull handlers (called by MediaTranscoder) ────────────────

        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (ReferenceEquals(args.Request.StreamDescriptor, _videoDesc))
                HandleVideoSampleRequest(args.Request);
            else if (ReferenceEquals(args.Request.StreamDescriptor, _audioDesc))
                HandleAudioSampleRequest(args.Request);
        }

        private void HandleVideoSampleRequest(MediaStreamSourceSampleRequest request)
        {
            if (_stopping) return; // null sample → end of stream

            var deferral = request.GetDeferral();
            try
            {
                while (!_stopping)
                {
                    if (_frameEvent.WaitOne(200))
                    {
                        using var frame = _framePool?.TryGetNextFrame();
                        if (frame != null)
                        {
                            // Pass the WGC surface directly — no CPU readback, no color conversion
                            // COM reference counting keeps the surface alive after frame.Dispose()
                            // Wall-clock timestamp ensures correct playback speed even if WGC drops frames
                            request.Sample = MediaStreamSample.CreateFromDirect3D11Surface(
                                frame.Surface,
                                _stopwatch.Elapsed);
                            request.Sample.Duration = TimeSpan.FromTicks(_frameDuration);
                            return;
                        }
                    }
                }
                // _stopping → don't set sample → end of stream
            }
            catch (Exception ex)
            {
                Log($"Video sample error: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void HandleAudioSampleRequest(MediaStreamSourceSampleRequest request)
        {
            if (_stopping || _audioQueue == null) return;

            var deferral = request.GetDeferral();
            try
            {
                (byte[] buf, int len, bool rented) item;

                if (!_audioQueue.TryTake(out item, 100))
                {
                    // No audio available yet — provide silence to keep the pipeline moving
                    int silenceBytes = _sampleRate * 4 / _fps; // ~1 frame's worth
                    item = (new byte[silenceBytes], silenceBytes, false);
                }

                try
                {
                    byte[] data = item.len == item.buf.Length
                        ? item.buf
                        : item.buf[..item.len];

                    var buffer = CryptographicBuffer.CreateFromByteArray(data);

                    int sampleCount = item.len / 4; // 2ch × 16bit = 4 bytes per sample
                    long duration = sampleCount * 10_000_000L / _sampleRate;

                    request.Sample = MediaStreamSample.CreateFromBuffer(
                        buffer,
                        TimeSpan.FromTicks(_audioTimestamp));
                    request.Sample.Duration = TimeSpan.FromTicks(duration);
                    _audioTimestamp += duration;
                }
                finally
                {
                    if (item.rented) ArrayPool<byte>.Shared.Return(item.buf);
                }
            }
            catch (Exception ex)
            {
                Log($"Audio sample error: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static int GetBitrate(int w, int h)
        {
            long pixels = (long)w * h;
            if (pixels >= 3840 * 2160) return 25_000_000; // 4K
            if (pixels >= 1920 * 1080) return 12_000_000; // 1080p
            if (pixels >= 1280 * 720)  return 8_000_000;  // 720p
            return 5_000_000; // SD
        }

        // ── WinRT D3D device interop ────────────────────────────────────────

        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
            SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private static IDirect3DDevice CreateWinRTDevice(Vortice.DXGI.IDXGIDevice dxgiDevice)
        {
            int hr = CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice.NativePointer, out IntPtr inspectable);
            Marshal.ThrowExceptionForHR(hr);
            var device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            return device;
        }

        // ── Cleanup ─────────────────────────────────────────────────────────

        private void CleanupAll()
        {
            try { _captureSession?.Dispose(); } catch { }
            _captureSession = null;
            try { _framePool?.Dispose(); } catch { }
            _framePool = null;
            _captureItem = null;

            if (_audioQueue != null)
            {
                try { _audioQueue.CompleteAdding(); } catch { }
                while (_audioQueue.TryTake(out var item))
                    if (item.rented) ArrayPool<byte>.Shared.Return(item.buf);
                _audioQueue.Dispose();
                _audioQueue = null;
            }

            _mediaStreamSource = null;
            _videoDesc = null;
            _audioDesc = null;

            try { _winrtDevice?.Dispose(); } catch { }
            _winrtDevice = null;
            try { _d3dContext?.Dispose(); } catch { }
            _d3dContext = null;
            try { _d3dDevice?.Dispose(); } catch { }
            _d3dDevice = null;
        }

        public void Dispose()
        {
            if (_isRecording) Stop();
            // Wait briefly for background transcode to finish before disposing the event
            try { _transcodeComplete?.Task.Wait(TimeSpan.FromSeconds(3)); } catch { }
            _frameEvent.Dispose();
            GC.SuppressFinalize(this);
        }

        ~WgcRecordingService() => Dispose();
    }
}
