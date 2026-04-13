using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public class RecordingService : IDisposable
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
        /// Probes whether FFmpeg supports h264_nvenc (NVIDIA hardware encoder).
        /// </summary>
        private static bool ProbeNvenc(string ffmpegPath)
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
                return output.Contains("h264_nvenc");
            }
            catch { return false; }
        }

        /// <summary>
        /// Start recording. Returns null on success or error message on failure.
        /// No FFmpeg process is spawned — raw frames are written directly to temp files.
        /// </summary>
        public string? Start(string outputPath, int width, int height, int fps,
            int sampleRate, string pixelFormat = "bgra", Action<string>? onEncodeComplete = null)
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

                // Encode in background — user can keep playing
                _encodeTask = Task.Run(() => EncodeAndMux(videoRaw, audioRaw, output, w, h, fps, sr, pf, frames, callback));

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
            long frameCount, Action<string>? onComplete)
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
                bool useNvenc = ProbeNvenc(ffmpegPath);
                string encoder = useNvenc
                    ? "-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -pix_fmt yuv420p"
                    : "-c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p";

                Trace.WriteLine($"[Recording] Encoding with {(useNvenc ? "NVENC (hardware)" : "x264 (software)")}");
                Trace.WriteLine($"[Recording] {frameCount} frames, {width}x{height}@{fps}fps");

                // Step 1: Encode raw video → temp MP4
                string encodeArgs =
                    $"-y " +
                    $"-f rawvideo -pixel_format {pixelFormat} -video_size {width}x{height} -framerate {fps} " +
                    $"-i \"{videoRaw}\" " +
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
                    string muxArgs =
                        $"-y " +
                        $"-i \"{tempMp4}\" " +
                        $"-f s16le -ar {sampleRate} -ac 2 -i \"{audioRaw}\" " +
                        $"-c:v copy -c:a aac -b:a 192k " +
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

            if (!pool.TryDequeue(out byte[]? frameBuf)) return; // drop frame

            int copyLen = Math.Min(length, frameBuf.Length);
            Buffer.BlockCopy(sourcePixels, 0, frameBuf, 0, copyLen);

            try
            {
                if (!q.TryAdd((frameBuf, copyLen)))
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
