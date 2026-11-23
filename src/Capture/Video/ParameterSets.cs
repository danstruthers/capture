namespace Capture.Video;

/// <summary>
/// Keeps an H.264 stream's latest SPS and PPS, and makes sure every key frame carries them.
/// Receivers that join mid-stream, and every new file, need them with the key frame, while some
/// encoders send them only once, at the start or only in their output media type.
/// </summary>
internal sealed class ParameterSets
{
    private byte[]? _sps;
    private byte[]? _pps;

    /// <summary>Start code + SPS + start code + PPS, once both are known.</summary>
    public byte[]? SequenceHeader { get; private set; }

    /// <summary>Takes note of parameter sets given outside the stream (an encoder's output type).</summary>
    public void Remember(ReadOnlySpan<byte> annexB)
    {
        AnnexB.AccessUnitInfo info = AnnexB.Inspect(annexB);
        Update(info.Sps, info.Pps);
    }

    /// <summary>
    /// Takes note of the parameter sets in an access unit and returns its bytes to send: a key
    /// frame without SPS and PPS gets them put in, ahead of the picture. Null for an access unit
    /// that has no picture (parameter sets on their own).
    /// </summary>
    public (byte[] Data, bool IsKeyFrame)? Complete(ReadOnlySpan<byte> accessUnit)
    {
        AnnexB.AccessUnitInfo info = AnnexB.Inspect(accessUnit);
        Update(info.Sps, info.Pps);
        if (!info.HasSlice)
        {
            return null;
        }

        if (!info.HasIdr || (info.Sps is not null && info.Pps is not null) || SequenceHeader is null)
        {
            return (accessUnit.ToArray(), info.HasIdr);
        }

        int at = info.ParameterSetOffset;
        byte[] bytes = new byte[SequenceHeader.Length + accessUnit.Length];
        accessUnit[..at].CopyTo(bytes);
        SequenceHeader.CopyTo(bytes, at);
        accessUnit[at..].CopyTo(bytes.AsSpan(at + SequenceHeader.Length));
        return (bytes, true);
    }

    private void Update(byte[]? sps, byte[]? pps)
    {
        bool changed = false;
        if (sps is not null && (_sps is null || !sps.AsSpan().SequenceEqual(_sps)))
        {
            _sps = sps;
            changed = true;
        }

        if (pps is not null && (_pps is null || !pps.AsSpan().SequenceEqual(_pps)))
        {
            _pps = pps;
            changed = true;
        }

        if (changed && _sps is not null && _pps is not null)
        {
            SequenceHeader = AnnexB.BuildSequenceHeader(_sps, _pps);
        }
    }
}
