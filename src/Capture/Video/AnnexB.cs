namespace Capture.Video;

/// <summary>H.264 Annex B byte-stream helpers: NAL units separated by 00 00 01 start codes.</summary>
internal static class AnnexB
{
    public const int NalSlice = 1;
    public const int NalIdrSlice = 5;
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalAccessUnitDelimiter = 9;

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>Location of one NAL unit; <see cref="Offset"/> points at its header byte.</summary>
    public readonly record struct Nal(int Offset, int Length, int Type);

    /// <summary>
    /// What an access unit contains, plus copies of any parameter sets found in it.
    /// <paramref name="ParameterSetOffset"/> is where missing parameter sets would be inserted: at the
    /// start, or after the access unit delimiter when there is one, which has to stay first.
    /// </summary>
    public readonly record struct AccessUnitInfo(bool HasIdr, bool HasSlice, byte[]? Sps, byte[]? Pps, int ParameterSetOffset);

    public static List<Nal> FindNals(ReadOnlySpan<byte> data)
    {
        var nals = new List<Nal>(8);
        int start = FindStartCode(data, 0, out int payload);

        while (start >= 0)
        {
            int next = FindStartCode(data, payload, out int nextPayload);
            int end = next >= 0 ? next : data.Length;

            // Zero bytes before the next start code belong to that start code, not to this NAL.
            while (end > payload && data[end - 1] == 0)
            {
                end--;
            }

            if (end > payload)
            {
                nals.Add(new Nal(payload, end - payload, data[payload] & 0x1f));
            }

            start = next;
            payload = nextPayload;
        }

        return nals;
    }

    public static AccessUnitInfo Inspect(ReadOnlySpan<byte> data)
    {
        bool hasIdr = false;
        bool hasSlice = false;
        byte[]? sps = null;
        byte[]? pps = null;

        List<Nal> nals = FindNals(data);
        int parameterSetOffset = 0;
        if (nals.Count > 1 && nals[0].Type == NalAccessUnitDelimiter)
        {
            // Back up over the next NAL's start code (00 00 01, possibly with one more leading zero).
            parameterSetOffset = nals[1].Offset - 3;
            if (parameterSetOffset > nals[0].Offset + nals[0].Length)
            {
                parameterSetOffset--;
            }
        }

        foreach (Nal nal in nals)
        {
            switch (nal.Type)
            {
                case NalIdrSlice:
                    hasIdr = true;
                    hasSlice = true;
                    break;
                case NalSlice:
                    hasSlice = true;
                    break;
                case NalSps:
                    sps ??= data.Slice(nal.Offset, nal.Length).ToArray();
                    break;
                case NalPps:
                    pps ??= data.Slice(nal.Offset, nal.Length).ToArray();
                    break;
            }
        }

        return new AccessUnitInfo(hasIdr, hasSlice, sps, pps, parameterSetOffset);
    }

    /// <summary>Builds "start code + SPS + start code + PPS", the form a decoder configuration is passed around in.</summary>
    public static byte[] BuildSequenceHeader(byte[] sps, byte[] pps)
    {
        byte[] header = new byte[StartCode.Length * 2 + sps.Length + pps.Length];
        int position = 0;
        position += Append(header, position, StartCode);
        position += Append(header, position, sps);
        position += Append(header, position, StartCode);
        Append(header, position, pps);
        return header;
    }

    private static int Append(byte[] destination, int position, byte[] source)
    {
        Buffer.BlockCopy(source, 0, destination, position, source.Length);
        return source.Length;
    }

    /// <summary>Finds the next 00 00 01 at or after <paramref name="from"/>; returns its index or -1.</summary>
    private static int FindStartCode(ReadOnlySpan<byte> data, int from, out int payloadStart)
    {
        ReadOnlySpan<byte> pattern = [0, 0, 1];
        int index = from <= data.Length ? data[from..].IndexOf(pattern) : -1;
        if (index < 0)
        {
            payloadStart = data.Length;
            return -1;
        }

        payloadStart = from + index + 3;
        return from + index;
    }
}
