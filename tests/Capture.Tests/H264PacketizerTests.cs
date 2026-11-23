using Capture.Output;

namespace Capture.Tests;

/// <summary>RTP packetization of H.264 (RFC 6184): small NAL units whole, large ones as FU-A fragments, the marker on the last packet.</summary>
public sealed class H264PacketizerTests
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>A NAL unit: its header byte (nal_ref_idc 3, the given type), then a counting payload.</summary>
    private static byte[] Nal(int type, int length) =>
        [(byte)(0x60 | type), .. Enumerable.Range(0, length - 1).Select(i => (byte)i)];

    private static byte[] AccessUnit(params byte[][] nals) => [.. nals.SelectMany(nal => StartCode.Concat(nal))];

    [Fact]
    public void Small_nal_units_go_out_whole_with_the_marker_on_the_last()
    {
        byte[] sps = Nal(7, 10), pps = Nal(8, 5), slice = Nal(5, 100);

        var packets = H264Packetizer.Packetize(AccessUnit(sps, pps, slice)).ToList();

        Assert.Equal([sps, pps, slice], packets.Select(packet => packet.Payload));
        Assert.Equal([false, false, true], packets.Select(packet => packet.Marker));
    }

    [Fact]
    public void A_large_nal_unit_is_split_into_fragments_that_rebuild_it()
    {
        byte[] slice = Nal(5, 2500);

        var packets = H264Packetizer.Packetize(AccessUnit(slice), maxPayload: 1000).ToList();

        Assert.Equal(3, packets.Count);
        Assert.All(packets, packet => Assert.True(packet.Payload.Length <= 1000));
        Assert.All(packets, packet => Assert.Equal(0x60 | 28, packet.Payload[0])); // FU-A, keeping the NAL's importance bits
        Assert.Equal([0x80 | 5, 5, 0x40 | 5], packets.Select(packet => (int)packet.Payload[1])); // start, middle, end
        Assert.Equal([false, false, true], packets.Select(packet => packet.Marker));

        byte[] rebuilt = [slice[0], .. packets.SelectMany(packet => packet.Payload.Skip(2))];
        Assert.Equal(slice, rebuilt);
    }

    [Fact]
    public void Only_the_last_fragment_of_the_last_nal_unit_carries_the_marker()
    {
        var packets = H264Packetizer.Packetize(AccessUnit(Nal(5, 1500), Nal(5, 50)), maxPayload: 1000).ToList();

        Assert.Equal([false, false, true], packets.Select(packet => packet.Marker));
    }
}
