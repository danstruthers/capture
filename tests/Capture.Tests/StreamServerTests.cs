using Capture.Output;

namespace Capture.Tests;

/// <summary>The stream server's checks that need no network: who may connect, and reading its own WebRTC offers.</summary>
public sealed class StreamServerTests
{
    [Theory]
    [InlineData(null, true)]                          // not a browser: no Origin header
    [InlineData("http://localhost:5174", true)]
    [InlineData("http://127.0.0.1:5174", true)]
    [InlineData("https://[::1]:8443", true)]
    [InlineData("http://localhost.example.com", false)] // looks local, is not
    [InlineData("http://192.168.1.20:5174", false)]
    [InlineData("null", false)]                       // a sandboxed page
    [InlineData("file:///C:/viewer/index.html", false)]
    public void Only_pages_served_from_this_machine_may_connect_unless_allowed(string? origin, bool allowed)
    {
        Assert.Equal(allowed, StreamServer.IsOriginAllowed(origin, []));
    }

    [Fact]
    public void Other_origins_can_be_allowed_one_by_one_or_all_at_once()
    {
        Assert.True(StreamServer.IsOriginAllowed("http://192.168.1.20:5174", ["http://192.168.1.20:5174"]));
        Assert.True(StreamServer.IsOriginAllowed("HTTP://192.168.1.20:5174", ["http://192.168.1.20:5174"]));
        Assert.False(StreamServer.IsOriginAllowed("http://192.168.1.21:5174", ["http://192.168.1.20:5174"]));
        Assert.True(StreamServer.IsOriginAllowed("https://anywhere.example", ["*"]));
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

        Dictionary<string, int> payloadTypes = WebRtcPeer.VideoPayloadTypesByMid(sdp);

        Assert.Equal(2, payloadTypes.Count);
        Assert.Equal(96, payloadTypes["1"]);
        Assert.Equal(97, payloadTypes["2"]);
    }
}
