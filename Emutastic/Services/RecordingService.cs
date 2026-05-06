using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    /// <summary>
    /// Encode-time settings for the FFmpeg recording path. Translated from
    /// <c>Configuration.RecordingConfiguration</c> by the caller so this
    /// service stays decoupled from the configuration layer.
    /// </summary>
    public class RecordingEncodeSettings
    {
        public string Quality { get; set; } = "High";   // Low / Medium / High / Lossless
        public int OutputScale { get; set; } = 2;       // 1..4
        public string Encoder { get; set; } = "Auto";   // Auto / NVENC / x264
        public bool HighChroma { get; set; } = false;
        public int AudioBitrateKbps { get; set; } = 192;

        /// <summary>
        /// Displayed aspect ratio reported by libretro (geometry.aspect_ratio).
        /// When > 0, scaling derives output width from height × aspect so non-
        /// square-pixel framebuffers (CD-i half-height interlaced, certain Genesis
        /// modes, etc.) come out at the correct viewing aspect instead of a stretched
        /// or squished mess. When 0/NaN, falls back to uniform integer scaling.
        /// </summary>
        public float DisplayAspectRatio { get; set; } = 0f;
    }

    public class RecordingService : IRecordingService
    {
        // During recording: raw frames go straight to temp files on disk.
        // No FFmpeg process runs during gameplay — zero CPU/GPU contention.
        // After recording stops, FFmpeg encodes the temp files (NVENC if available).
        private FileStream? _videoTempFile;
        private FileStream? _audioTempFile;

        private BlockingCollection<(byte[] buf, int len)>? _videoQueue;
        private BlockingCollection<(byte[] buf, int len, bool rented)>? _audioQueue;
        private Thread? _videoWriter;
        private Thread? _audioWriter;

        private volatile bool _isRecording;
        private volatile bool _stopping;
        private readonly object _lock = new();

        // Pre-allocated frame buffer pool — zero LOH allocations during recording
        private ConcurrentQueue<byte[]>? _framePool;
        private int _frameBufferSize;
        private const int FramePoolSize = 6;

        // Recording parameters
        private int _width, _height, _fps;
        private int _sampleRate;
        private string _pixelFormat = "bgra";
        private string _outputPath = "";
        private string _tempVideoPath = "";
        private string _tempAudioPath = "";
        private DateTime _startTime;
        private long _framesWritten;

        // Post-recording encode state
        private Task? _encodeTask;
        private Action<string>? _onEncodeComplete;
        private RecordingEncodeSettings _encodeSettings = new();

        public bool IsRecording => _isRecording;
        public bool IsEncoding => _encodeTask != null && !_encodeTask.IsCompleted;
        public TimeSpan Elapsed => _isRecording ? DateTime.Now - _startTime : TimeSpan.Zero;

        /// <summary>
        /// Finds ffmpeg.exe — checks app directory first, then PATH.
        /// </summary>
        public static string? FindFfmpeg()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                foreach (string dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// Probes which hardware H.264 encoders FFmpeg has compiled in.
        /// Detection here is "encoder is built into ffmpeg.exe" — actual
        /// runtime availability still depends on driver/GPU support, but
        /// missing detection is the most common cause of users getting
        /// software encoding when their GPU could do hardware.
        /// </summary>
        private static (bool nvenc, bool amf, bool qsv) ProbeHardwareEncoders(string ffmpegPath)
        {
            try
            {
                var probe = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-hide_banner -encoders",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };
                probe.Start();
                string output = probe.StandardOutput.ReadToEnd();
                probe.WaitForExit(5000);
                if (!probe.HasExited) { try { probe.Kill(); } catch { } }
                probe.Dispose();
                return (
                    nvenc: output.Contains("h264_nvenc"),
                    amf:   output.Contains("h264_amf"),
                    qsv:   output.Contains("h264_qsv")
                );
            }
            catch { return (false, false, false); }
        }

        /// <summary>
        /// Start recording. Returns null on success or error message on failure.
        /// No FFmpeg process is spawned — raw frames are written directly to temp files.
        /// </summary>
        public string? Start(string outputPath, int width, int height, int fps,
            int sampleRate, string pixelFormat = "bgra", Action<string>? onEncodeComplete = null,
            RecordingEncodeSettings? encodeSettings = null)
        {
            lock (_lock)
            {
                if (_isRecording) return "Already recording";

                string? ffmpegPath = FindFfmpeg();
                if (ffmpegPath == null) return "ffmpeg.exe not found";

                _outputPath = outputPath;
                _width = width;
                _height = height;
                _fps = fps > 0 ? fps : 60;
                _sampleRate = sampleRate > 0 ? sampleRate : 44100;
                _pixelFormat = pixelFormat;
                _stopping = false;
                _framesWritten = 0;
                _onEncodeComplete = onEncodeComplete;
                _encodeSettings = encodeSettings ?? new RecordingEncodeSettings();

                string? dir = Path.GetDirectoryName(outputPath);
                if (dir != null) Directory.CreateDirectory(dir);

                _tempVideoPath = outputPath + ".video.raw";
                _tempAudioPath = outputPath + ".audio.raw";

                // Pre-allocate frame buffer pool
                int bpp = pixelFormat == "rgb565le" ? 2 : 4;
                _frameBufferSize = width * height * bpp;
                _framePool = new ConcurrentQueue<byte[]>();
                for (int i = 0; i < FramePoolSize; i++)
                    _framePool.Enqueue(new byte[_frameBufferSize]);

                try
                {
                    // Open temp files — large buffers for sequential write throughput
                    _videoTempFile = new FileStream(_tempVideoPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 4 * 1024 * 1024, FileOptions.SequentialScan);
                    _audioTempFile = new FileStream(_tempAudioPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 65536);

                    // Bounded queues
                    _videoQueue = new BlockingCollection<(byte[], int)>(boundedCapacity: FramePoolSize);
                    _audioQueue = new BlockingCollection<(byte[], int, bool)>(boundedCapacity: 500);

                    // Writer threads — just sequential file I/O, no encoding
                    _videoWriter = new Thread(VideoWriterLoop) { Name = "RecordingVideoWriter", IsBackground = true };
                    _audioWriter = new Thread(AudioWriterLoop) { Name = "RecordingAudioWriter", IsBackground = true };
                    _videoWriter.Start();
                    _audioWriter.Start();

                    _startTime = DateTime.Now;
                    _isRecording = true;

                    Trace.WriteLine($"[Recording] Started: {_width}x{_height}@{_fps}fps, audio {_sampleRate}Hz");
                    Trace.WriteLine($"[Recording] Raw temp files: video={_tempVideoPath}, audio={_tempAudioPath}");
                    Trace.WriteLine($"[Recording] Frame pool: {FramePoolSize} x {_frameBufferSize / 1024}KB");
                    Trace.WriteLine($"[Recording] Data rate: {(long)_frameBufferSize * _fps / 1024 / 1024}MB/s to SSD");
                    return null;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Recording] Start failed: {ex.Message}");
                    CleanupResources(true);
                    return ex.Message;
                }
            }
        }

        /// <summary>
        /// Stop recording. Begins background FFmpeg encode of the temp files.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRecording || _stopping) return;
                _stopping = true;
                var elapsed = DateTime.Now - _startTime;
                _isRecording = false;

                Trace.WriteLine($"[Recording] Stopping after {elapsed:mm\\:ss} ({_framesWritten} frames)...");

                // Signal writer threads to drain and exit
                _videoQueue?.CompleteAdding();
                _audioQueue?.CompleteAdding();
                _videoWriter?.Join(5000);
                _audioWriter?.Join(5000);

                // Flush and close temp files
                try { _videoTempFile?.Flush(); _videoTempFile?.Close(); } catch { }
                _videoTempFile = null;
                try { _audioTempFile?.Flush(); _audioTempFile?.Close(); } catch { }
                _audioTempFile = null;

                // Clean up queues and pool
                DisposeQueues();

                // Capture params for the encode task.
                // Use actual FPS from frames written / duration to handle dropped frames.
                // If frames were dropped (pool exhaustion, display pending gate), encoding
                // at the target FPS would cause fast-forward playback.
                string videoRaw = _tempVideoPath;
                string audioRaw = _tempAudioPath;
                string output = _outputPath;
                int w = _width, h = _height, sr = _sampleRate;
                double actualFps = _framesWritten / Math.Max(elapsed.TotalSeconds, 0.1);
                int fps = Math.Max(1, (int)Math.Round(actualFps));
                string pf = _pixelFormat;
                long frames = _framesWritten;
                var callback = _onEncodeComplete;
                var settings = _encodeSettings;

                // Encode in background — user can keep playing
                _encodeTask = Task.Run(() => EncodeAndMux(videoRaw, audioRaw, output, w, h, fps, sr, pf, frames, callback, settings));

                _videoWriter = null;
                _audioWriter = null;

                Trace.WriteLine($"[Recording] Encoding started in background...");
            }
        }

        /// <summary>
        /// Background encode: raw temp files → MP4 via FFmpeg (NVENC if available).
        /// Called on a thread pool thread after recording stops.
        /// </summary>
        private static void EncodeAndMux(string videoRaw, string audioRaw, string outputPath,
            int width, int height, int fps, int sampleRate, string pixelFormat,
            long frameCount, Action<string>? onComplete, RecordingEncodeSettings settings)
        {
            string? ffmpegPath = FindFfmpeg();
            if (ffmpegPath == null)
            {
                onComplete?.Invoke("ffmpeg.exe not found for encoding");
                return;
            }

            string tempMp4 = outputPath + ".enc.mp4";

            try
            {
                // Resolve encoder. Two cases force x264 software encoding:
                //   - Lossless quality (no hardware H.264 encoder is truly
                //     lossless — they all apply in-loop deblocking at qp=0)
                //   - HighChroma (hardware H.264 encoders don't accept yuv422p;
                //     NVENC supports yuv444p but Chrome/Edge can't decode it)
                // Auto preference order for hardware: NVENC > AMF > QSV.
                var hw = ProbeHardwareEncoders(ffmpegPath);
                string activeEncoder; // "nvenc" | "amf" | "qsv" | "x264"

                if (settings.Quality == "Lossless" || settings.HighChroma)
                {
                    activeEncoder = "x264";
                }
                else
                {
                    activeEncoder = settings.Encoder switch
                    {
                        "NVENC" => hw.nvenc ? "nvenc" : "x264",
                        "AMF"   => hw.amf   ? "amf"   : "x264",
                        "QSV"   => hw.qsv   ? "qsv"   : "x264",
                        "x264"  => "x264",
                        _ => hw.nvenc ? "nvenc"
                           : hw.amf   ? "amf"
                           : hw.qsv   ? "qsv"
                           : "x264", // Auto
                    };
                }

                // Quality preset → quality values per encoder
                // x264 CRF: 0 lossless, 18 visually lossless, 23 default, 28 lower
                // NVENC CQ / AMF QP / QSV global_quality use similar 0–51 H.264 QP scales
                (int x264Crf, int hwQ, string nvencPreset, string amfQuality, string x264Preset) = settings.Quality switch
                {
                    "Low"      => (23, 26, "p3", "speed",    "veryfast"),
                    "Medium"   => (20, 22, "p4", "balanced", "fast"),
                    "Lossless" => (0,   0, "p7", "quality",  "veryslow"),
                    _          => (16, 19, "p5", "quality",  "medium"), // High (default)
                };

                // Pixel format selection:
                //   Lossless  → yuv444p (full chroma, x264 only — produces Hi444PP)
                //   HighChroma → yuv422p (sharper color edges than 420, broadly
                //                playable; 444 was rejected because Edge/Chrome/
                //                Windows Player won't decode High 4:4:4 reliably)
                //   default    → yuv420p (universal compatibility)
                string pixFmtOut;
                if (settings.Quality == "Lossless")
                    pixFmtOut = "yuv444p";
                else if (settings.HighChroma)
                    pixFmtOut = "yuv422p";
                else
                    pixFmtOut = "yuv420p";

                // h264_qsv only accepts nv12 / qsv input pix_fmts — yuv420p is rejected.
                // h264_amf accepts yuv420p directly (auto-converts to NV12 internally).
                // -qp_b on AMF: B-frames are auto-enabled by default; without an explicit
                // B-frame QP they encode unconstrained, drifting quality.
                string encoder = activeEncoder switch
                {
                    "nvenc" => $"-c:v h264_nvenc -preset {nvencPreset} -rc vbr -cq {hwQ} -pix_fmt {pixFmtOut}",
                    "amf"   => $"-c:v h264_amf -quality {amfQuality} -rc cqp -qp_i {hwQ} -qp_p {hwQ} -qp_b {hwQ} -pix_fmt {pixFmtOut}",
                    "qsv"   => $"-c:v h264_qsv -preset medium -global_quality {hwQ} -pix_fmt nv12",
                    _ => settings.Quality == "Lossless"
                        // x264 -qp 0 with veryslow is a true lossless stream.
                        ? $"-c:v libx264 -preset {x264Preset} -qp 0 -pix_fmt {pixFmtOut}"
                        : $"-c:v libx264 -preset {x264Preset} -crf {x264Crf} -pix_fmt {pixFmtOut}",
                };

                // Integer upscale at encode time using nearest-neighbor.
                // Sharper after platform re-encode (e.g. YouTube re-encodes
                // tiny frames with heavy quantization that smears pixel art).
                //
                // When the core reports a display aspect ratio that differs from
                // the framebuffer's pixel aspect (CD-i half-height, interlaced
                // modes, anamorphic Genesis hi-res), derive the target width from
                // height × aspect so the final video matches what's on screen.
                // Otherwise scale uniformly. H.264 needs even dimensions, so round.
                // Aspect correction is conservative: only fires when the framebuffer
                // pixel aspect is *dramatically* wrong vs the displayed aspect (>40%
                // off). That catches half-height interlaced modes (CD-i, some N64
                // resolutions) without disturbing typical retro consoles where the
                // framebuffer is square-ish but libretro reports a CRT-correct
                // display aspect — those recordings keep their existing pixel-
                // perfect look.
                int scale = Math.Clamp(settings.OutputScale, 1, 4);
                int targetW, targetH;
                float aspect = settings.DisplayAspectRatio;
                bool aspectValid = aspect > 0.1f && !float.IsNaN(aspect) && !float.IsInfinity(aspect);
                float fbAspect = (float)width / Math.Max(1, height);
                float aspectRatio = aspectValid ? Math.Max(aspect / fbAspect, fbAspect / aspect) : 1f;
                bool needsAspectFix = aspectValid && aspectRatio > 1.4f;

                if (needsAspectFix)
                {
                    targetH = height * scale;
                    targetW = (int)Math.Round(targetH * aspect);
                }
                else
                {
                    targetW = width  * scale;
                    targetH = height * scale;
                }
                if ((targetW & 1) != 0) targetW++;
                if ((targetH & 1) != 0) targetH++;

                bool needsScaleFilter = targetW != width || targetH != height;
                string scaleFilter = needsScaleFilter
                    ? $"-vf scale={targetW}:{targetH}:flags=neighbor "
                    : "";

                Trace.WriteLine($"[Recording] Encoding with {activeEncoder} " +
                                $"quality={settings.Quality} scale={scale}x pixfmt={pixFmtOut} " +
                                $"(hw probe: nvenc={hw.nvenc} amf={hw.amf} qsv={hw.qsv})");
                Trace.WriteLine($"[Recording] {frameCount} frames, {width}x{height}@{fps}fps " +
                                $"(fbAspect={fbAspect:F3} dispAspect={aspect:F3} fix={needsAspectFix}) → {targetW}x{targetH}");

                // Step 1: Encode raw video → temp MP4
                string encodeArgs =
                    $"-y " +
                    $"-f rawvideo -pixel_format {pixelFormat} -video_size {width}x{height} -framerate {fps} " +
                    $"-i \"{videoRaw}\" " +
                    $"-sws_flags neighbor " +
                    $"{scaleFilter}" +
                    $"{encoder} " +
                    $"-an " +
                    $"\"{tempMp4}\"";

                Trace.WriteLine($"[Recording] Encode cmd: {ffmpegPath} {encodeArgs}");

                var sw = Stopwatch.StartNew();
                var encode = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = encodeArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                    }
                };
                encode.Start();
                _ = encode.StandardError.BaseStream.CopyToAsync(Stream.Null);
                encode.WaitForExit(300000); // 5 minute timeout
                if (!encode.HasExited) { try { encode.Kill(); } catch { } }
                encode.Dispose();

                sw.Stop();
                Trace.WriteLine($"[Recording] Video encode took {sw.Elapsed.TotalSeconds:F1}s");

                if (!File.Exists(tempMp4))
                {
                    Trace.WriteLine("[Recording] Encode failed — no output file");
                    onComplete?.Invoke("Encoding failed");
                    return;
                }

                // Step 2: Mux video + audio → final MP4
                if (File.Exists(audioRaw) && new FileInfo(audioRaw).Length > 0)
                {
                    // Explicit -map: pull video from input 0 (tempMp4) and audio
                    // from input 1 (raw PCM). Without this, ffmpeg's auto stream
                    // selection can silently drop the audio track when the video
                    // uses an unusual H.264 profile (e.g. High 4:2:2 from yuv422p).
                    string muxArgs =
                        $"-y " +
                        $"-i \"{tempMp4}\" " +
                        $"-f s16le -ar {sampleRate} -ac 2 -i \"{audioRaw}\" " +
                        $"-map 0:v:0 -map 1:a:0 " +
                        $"-c:v copy -c:a aac -b:a {Math.Clamp(settings.AudioBitrateKbps, 64, 320)}k " +
                        $"-shortest " +
                        $"\"{outputPath}\"";

                    Trace.WriteLine($"[Recording] Mux cmd: {ffmpegPath} {muxArgs}");

                    var mux = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = ffmpegPath,
                            Arguments = muxArgs,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true,
                        }
                    };
                    mux.Start();
                    _ = mux.StandardError.BaseStream.CopyToAsync(Stream.Null);
                    mux.WaitForExit(60000);
                    if (!mux.HasExited) { try { mux.Kill(); } catch { } }
                    mux.Dispose();
                }
                else
                {
                    // No audio — just rename the encoded video
                    File.Move(tempMp4, outputPath, overwrite: true);
                }

                Trace.WriteLine($"[Recording] Saved: {outputPath}");
                onComplete?.Invoke(outputPath);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Recording] Encode/mux failed: {ex.Message}");
                onComplete?.Invoke($"Encoding failed: {ex.Message}");
            }
            finally
            {
                try { File.Delete(videoRaw); } catch { }
                try { File.Delete(audioRaw); } catch { }
                try { File.Delete(tempMp4); } catch { }
            }
        }

        // ── Frame queueing (called from emu thread) ────────────────────────

        /// <summary>
        /// Queue a video frame. Uses pre-allocated pool — zero allocations.
        /// If encoder is behind, the frame is silently dropped (never blocks emu thread).
        /// </summary>
        public void QueueVideoFrame(byte[] sourcePixels, int length)
        {
            var q = _videoQueue;
            var pool = _framePool;
            if (!_isRecording || q == null || q.IsAddingCompleted || pool == null) return;

            // Drop frames whose size doesn't match the pre-allocated buffer.
            // Truncating mid-recording would shift the byte stream and desync
            // every subsequent frame in the rawvideo temp file (diagonal tearing
            // in the final encode). Mismatched-dim frames are rare in practice
            // — Vectrex/CD-i dims are stable per session — but if the core
            // re-reports geometry mid-recording, dropping is the safe response.
            if (length != _frameBufferSize) return;

            if (!pool.TryDequeue(out byte[]? frameBuf)) return; // drop frame

            Buffer.BlockCopy(sourcePixels, 0, frameBuf, 0, length);

            try
            {
                if (!q.TryAdd((frameBuf, length)))
                    pool.Enqueue(frameBuf);
            }
            catch (InvalidOperationException)
            {
                pool.Enqueue(frameBuf);
            }
        }

        /// <summary>
        /// Queue audio samples. Audio buffers are small (~4KB) — ArrayPool is fine.
        /// </summary>
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

        // ── Writer threads (sequential file I/O only) ───────────────────────

        private void VideoWriterLoop()
        {
            try
            {
                foreach (var (buf, len) in _videoQueue!.GetConsumingEnumerable())
                {
                    try
                    {
                        _videoTempFile?.Write(buf, 0, len);
                        Interlocked.Increment(ref _framesWritten);
                    }
                    catch (IOException) { break; }
                    finally
                    {
                        _framePool?.Enqueue(buf);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Trace.WriteLine($"[Recording] Video writer error: {ex.Message}"); }
        }

        private void AudioWriterLoop()
        {
            try
            {
                foreach (var (buf, len, rented) in _audioQueue!.GetConsumingEnumerable())
                {
                    try
                    {
                        _audioTempFile?.Write(buf, 0, len);
                    }
                    catch (IOException) { break; }
                    finally
                    {
                        if (rented) ArrayPool<byte>.Shared.Return(buf);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Trace.WriteLine($"[Recording] Audio writer error: {ex.Message}"); }
        }

        // ── Cleanup ─────────────────────────────────────────────────────────

        private void DisposeQueues()
        {
            if (_videoQueue != null)
            {
                while (_videoQueue.TryTake(out var item))
                    _framePool?.Enqueue(item.buf);
                _videoQueue.Dispose();
                _videoQueue = null;
            }
            if (_audioQueue != null)
            {
                while (_audioQueue.TryTake(out var item))
                    if (item.rented) ArrayPool<byte>.Shared.Return(item.buf);
                _audioQueue.Dispose();
                _audioQueue = null;
            }
            _framePool = null;
        }

        private void CleanupResources(bool deleteTempFiles)
        {
            try { _videoTempFile?.Dispose(); } catch { }
            _videoTempFile = null;
            try { _audioTempFile?.Dispose(); } catch { }
            _audioTempFile = null;

            DisposeQueues();

            if (deleteTempFiles)
            {
                try { File.Delete(_tempVideoPath); } catch { }
                try { File.Delete(_tempAudioPath); } catch { }
            }

            _videoWriter = null;
            _audioWriter = null;
        }

        public void Dispose()
        {
            if (_isRecording) Stop();
            GC.SuppressFinalize(this);
        }

        ~RecordingService() => Dispose();
    }
}
