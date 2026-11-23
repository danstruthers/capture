using Capture.Video;

namespace Capture.Tests;

/// <summary>Every key frame has to carry the stream's SPS and PPS, even when the encoder sent them only once.</summary>
public sealed class ParameterSetsTests
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];
    private static readonly byte[] Sps = [0x67, 0x42, 0xC0, 0x1F];
    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];
    private static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x21];
    private static readonly byte[] Slice = [0x41, 0x9A, 0x02, 0x10];

    private static byte[] Unit(params byte[][] nals) => [.. nals.SelectMany(nal => StartCode.Concat(nal))];

    [Fact]
    public void A_key_frame_without_parameter_sets_gets_the_ones_seen_before()
    {
        var parameterSets = new ParameterSets();

        Assert.Null(parameterSets.Complete(Unit(Sps, Pps))); // parameter sets alone: nothing to show

        (byte[] data, bool isKeyFrame) = parameterSets.Complete(Unit(Idr))!.Value;
        Assert.True(isKeyFrame);
        Assert.Equal(Unit(Sps, Pps, Idr), data);
        Assert.Equal(Unit(Sps, Pps), parameterSets.SequenceHeader);
    }

    [Fact]
    public void Other_frames_and_complete_key_frames_go_out_as_they_are()
    {
        var parameterSets = new ParameterSets();
        parameterSets.Remember(Unit(Sps, Pps)); // from the encoder's output type

        (byte[] slice, bool sliceIsKey) = parameterSets.Complete(Unit(Slice))!.Value;
        Assert.Equal(Unit(Slice), slice);
        Assert.False(sliceIsKey);

        (byte[] keyFrame, bool keyFrameIsKey) = parameterSets.Complete(Unit(Sps, Pps, Idr))!.Value;
        Assert.Equal(Unit(Sps, Pps, Idr), keyFrame);
        Assert.True(keyFrameIsKey);
    }

    [Fact]
    public void New_parameter_sets_replace_the_old_ones()
    {
        byte[] newSps = [0x67, 0x64, 0x00, 0x28];
        var parameterSets = new ParameterSets();
        parameterSets.Remember(Unit(Sps, Pps));

        parameterSets.Complete(Unit(newSps, Pps, Idr));

        Assert.Equal(Unit(newSps, Pps, Idr), parameterSets.Complete(Unit(Idr))!.Value.Data);
    }
}
