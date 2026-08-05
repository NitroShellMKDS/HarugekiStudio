namespace HarugekiStudio.Audio;

public readonly record struct AudioMetadata(int SampleRate, int Channels, int BitsPerSample, long TotalSamples, int DataOffset, int DataLength);
