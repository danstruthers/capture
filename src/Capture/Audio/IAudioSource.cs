namespace Capture.Audio;

/// <summary>Something that records audio and places it, converted to <see cref="PcmFormat"/>, on a timeline.</summary>
internal interface IAudioSource : IDisposable
{
    /// <summary>The device's name.</summary>
    string Name { get; }

    /// <summary>The format the device delivers, for display: "48000 Hz, 2 ch, 32-bit float".</summary>
    string Format { get; }

    /// <summary>How long after a moment its audio can be relied on to be on the timeline, in 100 ns units.</summary>
    long Latency { get; }

    AudioTimeline Timeline { get; }

    /// <summary>Set if the source stopped by itself (device unplugged, format changed, ...).</summary>
    Exception? Failure { get; }
}
