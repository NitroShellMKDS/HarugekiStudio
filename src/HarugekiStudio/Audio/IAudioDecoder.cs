namespace HarugekiStudio.Audio;

public interface IAudioDecoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }
    long TotalSamples { get; }
    int TotalBytes { get; }
    long Position { get; set; }
    int Read(short[] buffer, int sampleCount);
    int Read(byte[] buffer, int sampleCount);
    void ReadAll(byte[] destination);
    void Seek(long samplePosition);
}
