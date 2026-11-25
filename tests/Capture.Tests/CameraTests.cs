using System.Collections.Concurrent;
using Capture.Video;
using MediaFoundation;

namespace Capture.Tests;

/// <summary>Choosing a camera and its mode, and (on a machine with a camera) capturing and encoding it.</summary>
[Collection(MediaFoundationCollection.Name)]
public sealed class CameraTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>What a typical USB camera offers: fast at the common sizes, slower at its largest, YUY2 only slowly.</summary>
    private static readonly CameraMode[] Modes =
    [
        new(MFMediaType.MJPG, 2048, 1536, 20),
        new(MFMediaType.NV12, 2048, 1536, 20),
        new(MFMediaType.YUY2, 1920, 1080, 5),
        new(MFMediaType.MJPG, 1920, 1080, 30),
        new(MFMediaType.NV12, 1920, 1080, 30),
        new(MFMediaType.NV12, 1280, 720, 30),
        new(MFMediaType.YUY2, 640, 480, 30),
        new(MFMediaType.MJPG, 640, 480, 60),
    ];

    private static readonly CameraDevice[] Cameras =
    [
        new(@"\\?\usb#vid_046d&pid_0825", "Logitech HD Webcam C270", 1),
        new(@"\\?\usb#vid_0c45&pid_6366", "USB Live Camera", 2),
        new(@"\\?\usb#vid_045e&pid_0728", "Microsoft LifeCam VX-5000", 3),
    ];

    [Theory]
    [InlineData(30, "NV12", 1920, 1080)] // the largest picture at that rate, uncompressed rather than MJPG
    [InlineData(15, "NV12", 2048, 1536)] // a slower rate allows the largest picture
    [InlineData(60, "MJPG", 640, 480)]   // only one mode is that fast
    [InlineData(1, "NV12", 2048, 1536)]
    public void The_mode_is_the_largest_picture_at_the_frame_rate(int fps, string format, int width, int height)
    {
        CameraMode mode = CameraDevices.ChooseMode(Modes, fps)!;

        Assert.Equal((format, width, height), (mode.Format, mode.Width, mode.Height));
    }

    [Theory]
    [InlineData(30, 1280, 720, "NV12", 1280, 720)] // 1920x1080 would give the same video, at more cost
    [InlineData(30, 640, 480, "YUY2", 640, 480)]   // uncompressed rather than MJPG
    [InlineData(15, 1280, 720, "NV12", 1280, 720)] // 2048x1536 fits as only 960x720
    [InlineData(30, 4096, 2304, "NV12", 1920, 1080)]
    public void The_mode_is_no_larger_than_the_video_needs(int fps, int maxWidth, int maxHeight, string format, int width, int height)
    {
        CameraMode mode = CameraDevices.ChooseMode(Modes, fps, maxWidth, maxHeight)!;

        Assert.Equal((format, width, height), (mode.Format, mode.Width, mode.Height));
    }

    [Fact]
    public void A_rate_no_mode_reaches_gets_the_fastest_there_is()
    {
        CameraMode[] slow = [new(MFMediaType.NV12, 1920, 1080, 15), new(MFMediaType.NV12, 640, 480, 29.97)];

        Assert.Equal(640, CameraDevices.ChooseMode(slow, 60)!.Width); // 29.97 counts as 30
        Assert.Null(CameraDevices.ChooseMode([], 30));
    }

    [Theory]
    [InlineData("2", "USB Live Camera")]
    [InlineData("usb  live", "USB Live Camera")] // runs of spaces count as one
    [InlineData("lifecam", "Microsoft LifeCam VX-5000")]
    [InlineData(@"\\?\usb#vid_046d&pid_0825", "Logitech HD Webcam C270")]
    public void A_camera_is_found_by_number_name_or_id(string selector, string name)
    {
        Assert.Equal(name, CameraDevices.Find(Cameras, selector).Name);
    }

    [Theory]
    [InlineData("4", "Camera 4 does not exist")]
    [InlineData("Kinect", "No camera has a name containing")]
    [InlineData("cam", "matches 3 cameras")]
    public void A_camera_that_is_not_there_is_reported(string selector, string expected)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => CameraDevices.Find(Cameras, selector));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Formats_are_shown_by_their_four_character_codes()
    {
        Assert.Equal("NV12", CameraDevices.FourCc(MFMediaType.NV12));
        Assert.Equal("MJPG", CameraDevices.FourCc(MFMediaType.MJPG));
        Assert.Equal("NV12 1920x1080 at 29.97 fps", new CameraMode(MFMediaType.NV12, 1920, 1080, 29.97).ToString());
    }

    [FactNeeding(Needs.Camera)]
    public void The_camera_delivers_pictures_only_while_they_are_wanted()
    {
        using var camera = new CameraCapturer(Machine.Cameras[0], 30);
        Assert.Equal((camera.Mode.Width & ~1, camera.Mode.Height & ~1), (camera.Width, camera.Height));
        Assert.Equal(0, camera.FramesReceived); // not opened until wanted
        Assert.False(camera.HasPicture);

        camera.SetActive(true);
        Assert.True(SpinWait.SpinUntil(() => camera.FramesReceived >= 10, Patience), $"no pictures from {camera.Name}: {camera.Problem}");
        Assert.True(camera.Capture(out string? problem), problem);
        Assert.True(camera.HasPicture);
        Assert.Null(camera.Problem);

        camera.SetActive(false);
        Thread.Sleep(500); // the reader finishes the picture it was waiting for, then closes the camera
        long closedAt = camera.FramesReceived;
        Thread.Sleep(500);
        Assert.Equal(closedAt, camera.FramesReceived);

        // The last picture is not live any more: the next time, nothing is sent until the camera delivers one.
        Assert.False(camera.HasPicture);
        camera.SetActive(true);
        Assert.True(SpinWait.SpinUntil(() => camera.Capture(out _) && camera.HasPicture, Patience), $"no pictures from {camera.Name} the second time: {camera.Problem}");
    }

    [FactNeeding(Needs.Camera)]
    public void A_camera_set_up_from_known_modes_is_not_opened_until_wanted()
    {
        using var first = new CameraCapturer(Machine.Cameras[0], 30);
        using var second = new CameraCapturer(Machine.Cameras[0], 15, (640, 480), first.Modes);

        Assert.Same(first.Modes, second.Modes);
        Assert.Equal(second.ModeFor(15), second.Mode);
        Assert.True(second.Mode.Width <= first.Mode.Width);
        Assert.Equal(0, second.FramesReceived);
    }

    [FactNeeding(Needs.Camera)]
    public void The_camera_is_encoded_at_its_own_frame_rate_beginning_with_a_key_frame()
    {
        var settings = new VideoSettings(
            Fps: 15,
            BitrateBps: null,
            GopSeconds: 2,
            H264Profile.Baseline,
            EncoderPreference.Auto,
            MaxWidth: 640,
            MaxHeight: 480,
            DrawCursor: false,
            CaptureMethod.Auto,
            JpegQuality: 70);

        using var frames = new BlockingCollection<EncodedVideoFrame>();
        using var pipeline = new VideoPipeline(new CameraCapturer(Machine.Cameras[0], settings.Fps), settings, new MediaClock());
        pipeline.FrameEncoded += frames.Add;
        pipeline.Start();
        pipeline.SetDemand(h264: true, jpeg: false);

        Assert.Equal(CameraDevices.StreamId, pipeline.Id);
        Assert.StartsWith("camera, ", pipeline.CaptureMethod);
        Assert.True(frames.TryTake(out EncodedVideoFrame? first, Patience), "no video from the camera");
        Assert.True(first!.IsKeyFrame, "the camera's video has to begin with a key frame");
        Assert.Equal(MediaClock.TicksPerSecond / 15, first.Duration);
        Assert.True(pipeline.Config.Width <= 640 && pipeline.Config.Height <= 480, $"{pipeline.Config.Width}x{pipeline.Config.Height} is not scaled to fit");
    }
}
