using Capture.Output;
using Capture.Video;

namespace Capture.Tests;

public class CaptureAreaTests
{
    // This development machine's arrangement: a 2560x1440 primary screen, and a 1080p screen to its left.
    private static readonly DisplayInfo Primary = new(0, @"\\.\DISPLAY1", 0, 0, 2560, 1440, IsPrimary: true);
    private static readonly DisplayInfo Left = new(1, @"\\.\DISPLAY2", -1920, 0, 1920, 1080, IsPrimary: false);

    private static VideoSettings Settings(int? maxWidth = null, int? maxHeight = null) =>
        new(30, null, 2, H264Profile.Baseline, EncoderPreference.Software, maxWidth, maxHeight, DrawCursor: false, CaptureMethod.Gdi, JpegQuality: 60);

    [Fact]
    public void Separate_layout_makes_one_area_per_screen_named_by_screen_number()
    {
        IReadOnlyList<CaptureArea> areas = CaptureArea.For(ScreenLayout.Separate, [Primary, Left]);

        Assert.Equal(["screen1", "screen2"], areas.Select(a => a.Id));
        Assert.Equal([1, 2], areas.Select(a => a.Number));
        Assert.Equal(["Screen 1", "Screen 2"], areas.Select(a => a.Name));
        Assert.True(areas[0].IsPrimary);
        Assert.False(areas[1].IsPrimary);
        Assert.Equal((-1920, 0, 1920, 1080), (areas[1].Left, areas[1].Top, areas[1].Width, areas[1].Height));
    }

    [Fact]
    public void Desktop_layout_makes_one_area_covering_every_screen()
    {
        CaptureArea desktop = Assert.Single(CaptureArea.For(ScreenLayout.Desktop, [Primary, Left]));

        Assert.Equal(CaptureArea.DesktopId, desktop.Id);
        Assert.Equal(0, desktop.Number);
        Assert.Equal((-1920, 0, 4480, 1440), (desktop.Left, desktop.Top, desktop.Width, desktop.Height));
        Assert.False(desktop.IsPrimary);
        Assert.Equal(2, desktop.Displays.Count);
    }

    [Fact]
    public void Video_keeps_the_screen_size_when_it_fits()
    {
        Assert.Equal((2560, 1440), VideoPipeline.ChooseOutputSize(CaptureArea.ForScreen(Primary), Settings()));
    }

    [Fact]
    public void Video_larger_than_encoders_accept_is_scaled_down_keeping_its_shape()
    {
        CaptureArea desktop = CaptureArea.ForDesktop([Primary, Left]);

        (int width, int height) = VideoPipeline.ChooseOutputSize(desktop, Settings());

        Assert.Equal(4096, width);
        Assert.Equal(1316, height); // 1440 * 4096 / 4480, made even
    }

    [Theory]
    [InlineData(1280, null, 1280, 720)]
    [InlineData(null, 540, 960, 540)]
    [InlineData(1280, 540, 960, 540)]   // the tighter limit wins
    [InlineData(5000, 3000, 2560, 1440)] // never scaled up
    public void Maximum_sizes_scale_the_video_down_keeping_its_shape(int? maxWidth, int? maxHeight, int width, int height)
    {
        Assert.Equal((width, height), VideoPipeline.ChooseOutputSize(CaptureArea.ForScreen(Primary), Settings(maxWidth, maxHeight)));
    }

    [Fact]
    public void Offer_sdp_maps_each_video_section_to_its_payload_type()
    {
        // The shape of an offer the capture client makes for two screens and audio (abridged).
        const string sdp = "v=0\r\n" +
            "a=group:BUNDLE 0 1 2\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 0 101\r\n" +
            "a=mid:0\r\n" +
            "a=rtpmap:0 PCMU/8000\r\n" +
            "a=rtpmap:101 telephone-event/8000\r\n" +
            "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
            "a=mid:1\r\n" +
            "a=rtpmap:96 H264/90000\r\n" +
            "a=fmtp:96 packetization-mode=1\r\n" +
            "m=video 9 UDP/TLS/RTP/SAVPF 97\r\n" +
            "a=mid:2\r\n" +
            "a=rtpmap:97 H264/90000\r\n";

        Dictionary<string, int> payloadTypes = StreamServer.VideoPayloadTypesByMid(sdp);

        Assert.Equal(2, payloadTypes.Count);
        Assert.Equal(96, payloadTypes["1"]);
        Assert.Equal(97, payloadTypes["2"]);
    }
}
