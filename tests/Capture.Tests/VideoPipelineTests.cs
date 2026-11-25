using System.Collections.Concurrent;
using Capture.Video;

namespace Capture.Tests;

/// <summary>
/// A video pipeline capturing the primary screen, scaled down: how it replaces its H.264 encoder
/// while running, and that the video goes on from a key frame each time.
/// </summary>
[Collection(MediaFoundationCollection.Name)]
public sealed class VideoPipelineTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [FactNeeding(Needs.Screen)]
    public void A_hardware_encoder_that_fails_is_replaced_by_the_software_encoder()
    {
        using var frames = new BlockingCollection<EncodedVideoFrame>();
        using VideoPipeline pipeline = StartPipeline(
            frames,
            EncoderPreference.Auto,
            (config, _) => new H264Encoder(new AsyncTransformSimulator { ProducesOutput = false }, activate: null, "simulated hardware encoder", isHardware: true, config));
        Assert.True(pipeline.EncoderIsHardware);

        // The encoder takes frames and gives nothing back; after three seconds of that it counts as failed.
        Assert.True(SpinWait.SpinUntil(() => !pipeline.EncoderIsHardware, Patience), "the failed encoder was not replaced");
        Assert.Equal(H264Encoders.SoftwareEncoderName, pipeline.EncoderName);

        Assert.True(frames.TryTake(out EncodedVideoFrame? first, Patience), "no video after the switch");
        Assert.True(first!.IsKeyFrame, "the video after the switch has to begin with a key frame");
    }

    [FactNeeding(Needs.Screen)]
    public void A_new_frame_rate_gets_a_new_encoder_whose_video_begins_with_a_key_frame()
    {
        using var frames = new BlockingCollection<EncodedVideoFrame>();
        using VideoPipeline pipeline = StartPipeline(frames, EncoderPreference.Software);
        Assert.True(frames.TryTake(out _, Patience), "no video");

        pipeline.SetFrameRate(5);
        Assert.Equal(5, pipeline.FrameRate);
        Assert.True(SpinWait.SpinUntil(() => pipeline.Config.Fps == 5, Patience), "the encoder was not replaced");

        // Frames the old encoder still held come out first; the new encoder's are a fifth of a second long.
        long fifth = MediaClock.TicksPerSecond / 5;
        EncodedVideoFrame? frame;
        do
        {
            Assert.True(frames.TryTake(out frame, Patience), "no video at the new frame rate");
        }
        while (frame!.Duration != fifth);

        Assert.True(frame.IsKeyFrame, "the video at the new frame rate has to begin with a key frame");
    }

    private static VideoPipeline StartPipeline(
        BlockingCollection<EncodedVideoFrame> frames,
        EncoderPreference encoder,
        Func<H264EncoderConfig, EncoderPreference, H264Encoder>? createEncoder = null)
    {
        DisplayInfo screen = Machine.Displays.FirstOrDefault(display => display.IsPrimary) ?? Machine.Displays[0];
        var settings = new VideoSettings(
            Fps: 15,
            BitrateBps: null,
            GopSeconds: 2,
            H264Profile.Baseline,
            encoder,
            MaxWidth: 640,
            MaxHeight: 360,
            DrawCursor: false,
            CaptureMethod.Gdi,
            JpegQuality: 70);

        var pipeline = new VideoPipeline(new ScreenCapturer(CaptureArea.ForScreen(screen), settings.DrawCursor, settings.CaptureMethod), settings, new MediaClock(), createEncoder);
        pipeline.FrameEncoded += frames.Add;
        pipeline.Start();
        pipeline.SetDemand(h264: true, jpeg: false);
        return pipeline;
    }
}
