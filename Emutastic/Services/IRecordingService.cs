using System;

namespace Emutastic.Services
{
    public interface IRecordingService : IDisposable
    {
        bool IsRecording { get; }
        bool IsEncoding { get; }
        TimeSpan Elapsed { get; }
        void QueueAudioSamples(byte[] sourceSamples, int length);
        void Stop();
    }
}
