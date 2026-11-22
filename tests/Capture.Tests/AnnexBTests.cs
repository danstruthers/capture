using Capture.Video;

namespace Capture.Tests;

public class AnnexBTests
{
    private static readonly byte[] Sps = [0x67, 0x42, 0xC0, 0x1F, 0x8C];
    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];
    private static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x00, 0x10];
    private static readonly byte[] Slice = [0x41, 0x9A, 0x02];

    [Fact]
    public void Finds_nal_units_behind_three_and_four_byte_start_codes()
    {
        byte[] stream = [0, 0, 0, 1, .. Sps, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr];

        List<AnnexB.Nal> nals = AnnexB.FindNals(stream);

        Assert.Equal([AnnexB.NalSps, AnnexB.NalPps, AnnexB.NalIdrSlice], nals.Select(n => n.Type));
        Assert.Equal(Sps, stream.AsSpan(nals[0].Offset, nals[0].Length).ToArray());
        Assert.Equal(Pps, stream.AsSpan(nals[1].Offset, nals[1].Length).ToArray());
        Assert.Equal(Idr, stream.AsSpan(nals[2].Offset, nals[2].Length).ToArray());
    }

    [Fact]
    public void Zero_bytes_before_a_start_code_are_not_part_of_the_preceding_nal()
    {
        byte[] stream = [0, 0, 1, .. Slice, 0, 0, 0, 0, 1, .. Slice];

        List<AnnexB.Nal> nals = AnnexB.FindNals(stream);

        Assert.Equal(2, nals.Count);
        Assert.All(nals, nal => Assert.Equal(Slice.Length, nal.Length));
    }

    [Fact]
    public void Data_without_a_start_code_has_no_nal_units()
    {
        Assert.Empty(AnnexB.FindNals([1, 2, 3, 4, 5]));
        Assert.Empty(AnnexB.FindNals([]));
    }

    [Fact]
    public void Inspect_reports_key_frames_and_extracts_parameter_sets()
    {
        byte[] keyFrame = [0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr];
        byte[] deltaFrame = [0, 0, 0, 1, .. Slice];
        byte[] parameterSetsOnly = [0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps];

        AnnexB.AccessUnitInfo key = AnnexB.Inspect(keyFrame);
        Assert.True(key.HasIdr);
        Assert.True(key.HasSlice);
        Assert.Equal(Sps, key.Sps);
        Assert.Equal(Pps, key.Pps);

        AnnexB.AccessUnitInfo delta = AnnexB.Inspect(deltaFrame);
        Assert.False(delta.HasIdr);
        Assert.True(delta.HasSlice);
        Assert.Null(delta.Sps);

        Assert.False(AnnexB.Inspect(parameterSetsOnly).HasSlice);
    }

    [Fact]
    public void Missing_parameter_sets_belong_after_a_leading_access_unit_delimiter()
    {
        byte[] delimiter = [0x09, 0x10];
        byte[] withDelimiter = [0, 0, 0, 1, .. delimiter, 0, 0, 0, 1, .. Idr];
        byte[] withShortStartCodes = [0, 0, 1, .. delimiter, 0, 0, 1, .. Idr];
        byte[] withoutDelimiter = [0, 0, 0, 1, .. Idr];

        Assert.Equal(6, AnnexB.Inspect(withDelimiter).ParameterSetOffset);
        Assert.Equal(5, AnnexB.Inspect(withShortStartCodes).ParameterSetOffset);
        Assert.Equal(0, AnnexB.Inspect(withoutDelimiter).ParameterSetOffset);
    }

    [Fact]
    public void Sequence_header_is_sps_then_pps_each_behind_a_start_code()
    {
        byte[] header = AnnexB.BuildSequenceHeader(Sps, Pps);

        Assert.Equal([0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps], header);
    }
}
