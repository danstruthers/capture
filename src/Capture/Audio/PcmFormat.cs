namespace Capture.Audio;

/// <summary>The audio format every part of the application works in: 16-bit linear PCM, 8 kHz, mono.</summary>
internal static class PcmFormat
{
    public const int SampleRate = 8000;
    public const int Channels = 1;

    /// <summary>Samples in one 20 ms frame.</summary>
    public const int SamplesPerFrame = SampleRate / 50;

    /// <summary>Length of one frame in 100 ns units.</summary>
    public const long FrameDuration = MediaClock.TicksPerSecond / 50;

    public static long TimeToSample(long time) => time * SampleRate / MediaClock.TicksPerSecond;

    /// <summary>A device's own format, for display: "48000 Hz, 2 ch, 32-bit float".</summary>
    /// <param name="sampleType">"float", "PCM", or what else the device delivers.</param>
    public static string Describe(int sampleRate, int channels, int bitsPerSample, string sampleType) =>
        $"{sampleRate} Hz, {channels} ch, {bitsPerSample}-bit {sampleType}";
}

/// <summary>20 ms of audio in <see cref="PcmFormat"/>.</summary>
internal sealed class AudioFrame
{
    public required short[] Samples { get; init; }

    /// <summary>Capture time of the first sample on the session clock (100 ns units).</summary>
    public required long Timestamp { get; init; }

    public long Duration => PcmFormat.FrameDuration;
}
