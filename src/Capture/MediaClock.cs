using System.Diagnostics;

namespace Capture;

/// <summary>
/// The single timeline that video and audio timestamps are taken from, in Media Foundation
/// units (100 ns ticks) counted from when the capture session started.
/// </summary>
internal sealed class MediaClock
{
    public const long TicksPerSecond = 10_000_000;

    private readonly long _origin = Stopwatch.GetTimestamp();

    public long Now => (long)((Stopwatch.GetTimestamp() - _origin) * ((double)TicksPerSecond / Stopwatch.Frequency));

    public static long FromSeconds(double seconds) => (long)(seconds * TicksPerSecond);

    public static double ToMilliseconds(long ticks) => ticks / 10_000.0;
}
