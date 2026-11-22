using Capture.Audio;

namespace Capture.Tests;

public class AudioTimelineTests
{
    private const long Millisecond = 10_000; // session clock ticks

    private static short[] Ramp(int count, short first = 1) =>
        [.. Enumerable.Range(0, count).Select(i => (short)(first + i))];

    private static short[] Read(AudioTimeline timeline, long startSample, int count)
    {
        short[] samples = new short[count];
        timeline.Read(startSample, samples);
        return samples;
    }

    [Fact]
    public void Samples_are_read_back_at_the_time_they_were_captured()
    {
        var timeline = new AudioTimeline();

        timeline.Write(1000 * Millisecond, Ramp(160)); // one second in = sample 8000

        Assert.Equal(Ramp(160), Read(timeline, 8000, 160));
    }

    [Fact]
    public void Anything_never_written_reads_as_silence()
    {
        var timeline = new AudioTimeline();
        timeline.Write(1000 * Millisecond, Ramp(160));

        Assert.All(Read(timeline, 0, 160), sample => Assert.Equal(0, sample));       // before the data
        Assert.All(Read(timeline, 8160, 160), sample => Assert.Equal(0, sample));    // after the data
        Assert.Equal(new short[160], Read(new AudioTimeline(), 0, 160));             // nothing written at all
    }

    [Fact]
    public void Blocks_with_slightly_uneven_timestamps_stay_contiguous()
    {
        var timeline = new AudioTimeline();

        // Second block claims to start 5 ms late; that is jitter, not a gap.
        timeline.Write(1000 * Millisecond, Ramp(160));
        timeline.Write(1025 * Millisecond, Ramp(160, first: 1000));

        Assert.Equal([.. Ramp(160), .. Ramp(160, first: 1000)], Read(timeline, 8000, 320));
    }

    [Fact]
    public void A_source_that_skips_ahead_leaves_silence_in_between()
    {
        var timeline = new AudioTimeline();

        timeline.Write(1000 * Millisecond, Ramp(160));
        timeline.Write(1520 * Millisecond, Ramp(160, first: 1000)); // 500 ms after the first block ended

        Assert.Equal(Ramp(160), Read(timeline, 8000, 160));
        Assert.All(Read(timeline, 8160, 4000), sample => Assert.Equal(0, sample));
        Assert.Equal(Ramp(160, first: 1000), Read(timeline, 12160, 160));
    }

    [Fact]
    public void A_gap_does_not_replay_audio_from_one_lap_of_the_buffer_earlier()
    {
        var timeline = new AudioTimeline();

        // Fill four seconds (the whole ring) with a non-zero value, then skip one second ahead.
        short[] loud = [.. Enumerable.Repeat((short)1234, 8000)];
        for (int second = 0; second < 4; second++)
        {
            timeline.Write(second * 1000 * Millisecond, loud);
        }

        timeline.Write(5000 * Millisecond, Ramp(160));

        Assert.All(Read(timeline, 4 * 8000, 8000), sample => Assert.Equal(0, sample));
        Assert.Equal(Ramp(160), Read(timeline, 5 * 8000, 160));
    }

    [Fact]
    public void Audio_older_than_the_buffer_is_gone()
    {
        var timeline = new AudioTimeline();
        short[] loud = [.. Enumerable.Repeat((short)1234, 8000)];
        for (int second = 0; second < 6; second++)
        {
            timeline.Write(second * 1000 * Millisecond, loud);
        }

        Assert.All(Read(timeline, 0, 8000), sample => Assert.Equal(0, sample));          // overwritten long ago
        Assert.All(Read(timeline, 5 * 8000, 8000), sample => Assert.Equal(1234, sample)); // still there
    }
}
