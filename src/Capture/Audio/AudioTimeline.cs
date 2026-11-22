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
}

/// <summary>20 ms of audio in <see cref="PcmFormat"/>.</summary>
internal sealed class AudioFrame
{
    public required short[] Samples { get; init; }

    /// <summary>Capture time of the first sample on the session clock (100 ns units).</summary>
    public required long Timestamp { get; init; }

    public long Duration => PcmFormat.FrameDuration;
}

/// <summary>
/// The last few seconds of one source's audio, addressed by position on the session clock rather
/// than by arrival order. Sources write when their data turns up (in bursts, late, or not at all
/// while nothing is playing); the mixer reads fixed windows a little behind real time. Anything a
/// source never wrote reads back as silence.
/// </summary>
internal sealed class AudioTimeline
{
    private const int Capacity = PcmFormat.SampleRate * 4;

    /// <summary>
    /// How far a source's own timestamps may wander from its running sample count before the count
    /// is re-anchored. Smaller jitter is ignored so that normal audio stays sample-contiguous; a
    /// larger difference means the source really skipped (silence, lost data, clock drift).
    /// </summary>
    private const int ResyncThreshold = PcmFormat.SampleRate * 40 / 1000;

    private readonly short[] _ring = new short[Capacity];
    private readonly object _lock = new();
    private long _end = -1; // absolute index one past the newest sample; -1 until the first write

    /// <summary>Adds samples that were captured starting at <paramref name="captureTime"/> on the session clock.</summary>
    public void Write(long captureTime, ReadOnlySpan<short> samples)
    {
        long position = Math.Max(0, PcmFormat.TimeToSample(captureTime));

        lock (_lock)
        {
            if (_end < 0)
            {
                _end = position;
            }
            else if (position - _end > ResyncThreshold)
            {
                // The source jumped ahead. The skipped stretch must read as silence, not as whatever
                // was stored there one lap of the ring ago.
                Silence(_end, Math.Min(position - _end, Capacity));
                _end = position;
            }
            else if (_end - position > ResyncThreshold)
            {
                _end = position;
            }

            foreach (short sample in samples)
            {
                _ring[_end % Capacity] = sample;
                _end++;
            }
        }
    }

    /// <summary>Copies the samples at absolute positions [start, start + destination.Length); gaps are silence.</summary>
    public void Read(long start, Span<short> destination)
    {
        lock (_lock)
        {
            long oldest = _end - Capacity;
            for (int i = 0; i < destination.Length; i++)
            {
                long index = start + i;
                destination[i] = index >= 0 && index >= oldest && index < _end ? _ring[index % Capacity] : (short)0;
            }
        }
    }

    private void Silence(long start, long count)
    {
        for (long i = 0; i < count; i++)
        {
            _ring[(start + i) % Capacity] = 0;
        }
    }
}
