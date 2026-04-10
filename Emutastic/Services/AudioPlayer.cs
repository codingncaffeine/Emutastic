using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;
using NAudio.Wasapi;
using NAudio.CoreAudioApi;

namespace Emutastic.Services
{
    public class AudioPlayer : IDisposable
    {
        private readonly Queue<short> _audioBuffer = new Queue<short>();
        private readonly object _lock = new object();
        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _waveProvider;
        private bool _isPlaying = false;
        private readonly int _sampleRate;
        private const int Channels = 2;

        // Always output at this rate. WdlResamplingSampleProvider converts the core's
        // native rate (e.g. ~33075 Hz for N64) to a standard rate so WaveOut doesn't
        // have to rely on the audio driver's resampler, which can produce artefacts.
        private const int OutputSampleRate = 44100;

        /// <summary>
        /// WaveOut desired latency in ms. Set before calling Start().
        /// Higher values absorb FPS dips better (Vulkan readback cores).
        /// </summary>
        public int DesiredLatencyMs { get; set; } = 120;

        public AudioPlayer(int sampleRate = 44100)
        {
            _sampleRate = sampleRate;
        }

        public void Start()
        {
            if (_isPlaying) return;

            try
            {
                // Input buffer: core's native sample rate (e.g. ~33075 Hz for N64).
                _waveProvider = new BufferedWaveProvider(new WaveFormat(_sampleRate, 16, Channels))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true
                };

                // Build the playback chain: input buffer → (optional resample) → WaveOut.
                // We always output at OutputSampleRate (44100 Hz) so WaveOut opens the
                // device at a standard rate and no driver-level resampling is needed.
                IWaveProvider playbackChain;
                if (_sampleRate == OutputSampleRate)
                {
                    playbackChain = _waveProvider;
                }
                else
                {
                    // Wdl sinc resampler: high-quality, pure .NET, no COM/MF required.
                    var floatIn  = _waveProvider.ToSampleProvider();
                    var resampled = new WdlResamplingSampleProvider(floatIn, OutputSampleRate);
                    playbackChain = new SampleToWaveProvider16(resampled);
                }

                _waveOut = new WaveOutEvent
                {
                    DesiredLatency = DesiredLatencyMs,
                    DeviceNumber = -1
                };

                _waveOut.Init(playbackChain);
                // Do NOT call Play() here — the emulation loop pre-fills the buffer first,
                // then calls BeginPlayback() so WaveOut never starts from an empty buffer.
                _isPlaying = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio start error: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (!_isPlaying) return;

            try
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;
                _waveProvider = null;
                _isPlaying = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio stop error: {ex.Message}");
            }
        }

        public void QueueSample(short left, short right)
        {
            if (!_isPlaying || _waveProvider == null) return;

            byte[] bytes = new byte[4];
            Buffer.BlockCopy(new short[] { left, right }, 0, bytes, 0, 4);

            lock (_lock)
            {
                if (_waveProvider == null) return;
                if (_waveProvider.BufferedBytes < _waveProvider.BufferLength - 4)
                    _waveProvider.AddSamples(bytes, 0, 4);
                // Drop sample if buffer full — frame timing is handled by the emulation loop timer.
            }
        }

        public void QueueBatch(short[] samples)
        {
            if (!_isPlaying || _waveProvider == null) return;

            byte[] bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

            lock (_lock)
            {
                if (_waveProvider == null) return;
                int available = _waveProvider.BufferLength - _waveProvider.BufferedBytes;
                int toWrite = Math.Min(bytes.Length, available);
                if (toWrite > 0)
                    _waveProvider.AddSamples(bytes, 0, toWrite);
            }
        }

        /// <summary>
        /// Zero-copy path: accepts a pre-allocated byte buffer from the caller.
        /// Avoids the short[]→byte[] conversion and heap allocation in the hot path.
        /// </summary>
        public void QueueBatchBytes(byte[] bytes, int byteCount)
        {
            if (!_isPlaying || _waveProvider == null) return;

            lock (_lock)
            {
                if (_waveProvider == null) return;
                int available = _waveProvider.BufferLength - _waveProvider.BufferedBytes;
                int toWrite = Math.Min(byteCount, available);
                if (toWrite > 0)
                    _waveProvider.AddSamples(bytes, 0, toWrite);
            }
        }

        /// <summary>
        /// Starts WaveOut playback. Call this after the buffer has been pre-filled so that
        /// WaveOut never reads from an empty buffer and produces an initial underrun crackle.
        /// </summary>
        public void BeginPlayback()
        {
            if (_waveOut != null && _isPlaying)
                _waveOut.Play();
        }

        /// <summary>
        /// Returns the amount of audio currently queued in the output buffer, in milliseconds.
        /// Used only as a backpressure signal by the emulation loop — not as the primary clock.
        /// The step-function behaviour of BufferedBytes (~20ms steps from WaveOut drain callbacks)
        /// is acceptable here because the Stopwatch in EmulationLoop drives frame timing.
        /// </summary>
        public int GetBufferedMs()
        {
            var provider = _waveProvider;
            if (provider == null) return 0;
            return (int)((long)provider.BufferedBytes * 1000 / (_sampleRate * Channels * 2));
        }

        public void Dispose()
        {
            Stop();
        }
    }
}