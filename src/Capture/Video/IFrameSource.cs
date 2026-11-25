namespace Capture.Video;

/// <summary>
/// What a <see cref="VideoPipeline"/> takes its pictures from: a screen area (<see cref="ScreenCapturer"/>)
/// or a camera (<see cref="CameraCapturer"/>). Either way a picture is a top-down 32-bit BGRX bitmap
/// of a fixed size, which the pipeline reads at its own frame rate.
/// </summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>The stream id: "screen1", "desktop", "camera".</summary>
    string Id { get; }

    /// <summary>For people: "Screen 1", or the camera's name.</summary>
    string Name { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>The picture: top-down BGRX, <c>Width * 4</c> bytes per row. Valid until the next <see cref="Capture"/>.</summary>
    IntPtr Pixels { get; }

    int SizeInBytes { get; }

    /// <summary>How the pictures are read, for people: "DXGI desktop duplication", "camera, NV12 1920x1080 at 30 fps".</summary>
    string Method { get; }

    /// <summary>
    /// Whether <see cref="Pixels"/> holds a picture worth sending. A camera has none from the moment
    /// it is opened until its first picture arrives; the pipeline sends nothing until then.
    /// </summary>
    bool HasPicture { get; }

    /// <summary>
    /// Puts the newest picture in <see cref="Pixels"/>. Returns false when there is none to be had
    /// right now (the lock screen, a camera that was unplugged), with <paramref name="problem"/> saying
    /// why; the previous picture then stays in place.
    /// </summary>
    bool Capture(out string? problem);

    /// <summary>
    /// Whether pictures are wanted. The pipeline says so whenever that changes; a camera is only
    /// open (and its light on) while they are.
    /// </summary>
    void SetActive(bool active);
}
