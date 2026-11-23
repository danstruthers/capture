using Capture.Video;

namespace Capture.Output;

/// <summary>Packs H.264 access units into RTP payloads (RFC 6184, packetization mode 1).</summary>
internal static class H264Packetizer
{
    /// <summary>Largest RTP payload sent: leaves room for the RTP, SRTP, UDP and IP headers within a 1500-byte network packet.</summary>
    public const int MaxPayload = 1200;

    private const int FuA = 28;

    /// <summary>
    /// The RTP payloads for one Annex B access unit, in order. A NAL unit that fits goes out as it
    /// is (a single NAL unit packet); a larger one is split into FU-A fragments. The last payload
    /// has the marker set: it ends the access unit.
    /// </summary>
    public static IEnumerable<(byte[] Payload, bool Marker)> Packetize(byte[] accessUnit, int maxPayload = MaxPayload)
    {
        List<AnnexB.Nal> nals = AnnexB.FindNals(accessUnit);
        for (int n = 0; n < nals.Count; n++)
        {
            AnnexB.Nal nal = nals[n];
            bool lastNal = n == nals.Count - 1;

            if (nal.Length <= maxPayload)
            {
                yield return (accessUnit.AsSpan(nal.Offset, nal.Length).ToArray(), lastNal);
                continue;
            }

            // FU indicator: the NAL's forbidden and importance bits with type 28; FU header: start
            // and end flags with the NAL's own type. The NAL header byte itself is not repeated.
            byte header = accessUnit[nal.Offset];
            byte indicator = (byte)((header & 0xE0) | FuA);
            int type = header & 0x1F;
            int offset = nal.Offset + 1;
            int remaining = nal.Length - 1;
            bool first = true;

            while (remaining > 0)
            {
                int size = Math.Min(remaining, maxPayload - 2);
                bool last = size == remaining;

                byte[] payload = new byte[size + 2];
                payload[0] = indicator;
                payload[1] = (byte)(type | (first ? 0x80 : 0) | (last ? 0x40 : 0));
                Buffer.BlockCopy(accessUnit, offset, payload, 2, size);
                yield return (payload, lastNal && last);

                offset += size;
                remaining -= size;
                first = false;
            }
        }
    }
}
