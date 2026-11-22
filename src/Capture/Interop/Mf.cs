using System.Runtime.InteropServices;
using MediaFoundation;

namespace Capture.Interop;

internal sealed class MfException : Exception
{
    public MfException(string operation, HResult code)
        : base($"{operation} failed: {Mf.Describe(code)}")
    {
        Code = code;
        HResult = (int)code;
    }

    public HResult Code { get; }
}

/// <summary>Small helpers over the MediaFoundation wrapper: error checks, COM release, types and samples.</summary>
internal static unsafe class Mf
{
    private const int MfVersion = 0x00020070;
    private const int Align64 = 0x3f;

    public static void Startup() => Check(MFExtern.MFStartup(MfVersion, MFStartup.Full), "MFStartup");

    public static void Shutdown() => MFExtern.MFShutdown();

    public static bool Failed(HResult hr) => (int)hr < 0;

    public static void Check(HResult hr, string operation)
    {
        if ((int)hr < 0)
        {
            throw new MfException(operation, hr);
        }
    }

    public static string Describe(HResult hr) =>
        Enum.IsDefined(hr) ? $"{hr} (0x{(int)hr:X8})" : $"0x{(int)hr:X8}";

    /// <summary>
    /// Releases a COM object now instead of when the GC gets to it. Frame buffers are megabytes of
    /// native memory the GC cannot see, so everything on the per-frame path is released explicitly.
    /// </summary>
    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    public static void Release<T>(ref T? com) where T : class
    {
        Release(com);
        com = null;
    }

    public static IMFAttributes CreateAttributes(int initialSize = 4)
    {
        Check(MFExtern.MFCreateAttributes(out IMFAttributes attributes, initialSize), "MFCreateAttributes");
        return attributes;
    }

    public static IMFMediaType CreateVideoType(Guid subtype, int width, int height, int fps)
    {
        Check(MFExtern.MFCreateMediaType(out IMFMediaType type), "MFCreateMediaType");
        Check(type.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Video), "Set MF_MT_MAJOR_TYPE");
        Check(type.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, subtype), "Set MF_MT_SUBTYPE");
        Check(MFExtern.MFSetAttributeSize(type, MFAttributesClsid.MF_MT_FRAME_SIZE, width, height), "Set MF_MT_FRAME_SIZE");
        Check(MFExtern.MFSetAttributeRatio(type, MFAttributesClsid.MF_MT_FRAME_RATE, fps, 1), "Set MF_MT_FRAME_RATE");
        Check(MFExtern.MFSetAttributeRatio(type, MFAttributesClsid.MF_MT_PIXEL_ASPECT_RATIO, 1, 1), "Set MF_MT_PIXEL_ASPECT_RATIO");
        Check(type.SetUINT32(MFAttributesClsid.MF_MT_INTERLACE_MODE, (int)MFVideoInterlaceMode.Progressive), "Set MF_MT_INTERLACE_MODE");
        return type;
    }

    /// <summary>
    /// Tags a YUV type as limited-range BT.709 (HD) or BT.601 (SD), which is what decoders assume
    /// for those sizes when the bitstream does not say otherwise.
    /// </summary>
    public static void SetYuvColorInfo(IMFMediaType type, bool bt709)
    {
        type.SetUINT32(MFAttributesClsid.MF_MT_YUV_MATRIX, (int)(bt709 ? MFVideoTransferMatrix.BT709 : MFVideoTransferMatrix.BT601));
        type.SetUINT32(MFAttributesClsid.MF_MT_VIDEO_PRIMARIES, (int)(bt709 ? MFVideoPrimaries.BT709 : MFVideoPrimaries.SMPTE170M));
        type.SetUINT32(MFAttributesClsid.MF_MT_TRANSFER_FUNCTION, (int)MFVideoTransferFunction.Func709);
        type.SetUINT32(MFAttributesClsid.MF_MT_VIDEO_NOMINAL_RANGE, (int)MFNominalRange.MFNominalRange_16_235);
    }

    /// <summary>Uncompressed 16-bit integer PCM.</summary>
    public static IMFMediaType CreatePcmType(int sampleRate, int channels)
    {
        int blockAlign = channels * 2;
        Check(MFExtern.MFCreateMediaType(out IMFMediaType type), "MFCreateMediaType");
        Check(type.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio), "Set MF_MT_MAJOR_TYPE");
        Check(type.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.PCM), "Set MF_MT_SUBTYPE");
        Check(type.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, channels), "Set MF_MT_AUDIO_NUM_CHANNELS");
        Check(type.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate), "Set MF_MT_AUDIO_SAMPLES_PER_SECOND");
        Check(type.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BITS_PER_SAMPLE, 16), "Set MF_MT_AUDIO_BITS_PER_SAMPLE");
        Check(type.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BLOCK_ALIGNMENT, blockAlign), "Set MF_MT_AUDIO_BLOCK_ALIGNMENT");
        Check(type.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sampleRate * blockAlign), "Set MF_MT_AUDIO_AVG_BYTES_PER_SECOND");
        Check(type.SetUINT32(MFAttributesClsid.MF_MT_ALL_SAMPLES_INDEPENDENT, 1), "Set MF_MT_ALL_SAMPLES_INDEPENDENT");
        return type;
    }

    public static IMFMediaBuffer CreateBuffer(int size)
    {
        Check(MFExtern.MFCreateAlignedMemoryBuffer(size, Align64, out IMFMediaBuffer buffer), "MFCreateAlignedMemoryBuffer");
        return buffer;
    }

    public static IMFSample CreateSample(IMFMediaBuffer buffer)
    {
        Check(MFExtern.MFCreateSample(out IMFSample sample), "MFCreateSample");
        Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
        return sample;
    }

    /// <summary>Creates a sample holding a copy of <paramref name="data"/>.</summary>
    public static IMFSample CreateSample(ReadOnlySpan<byte> data, long time, long duration)
    {
        IMFMediaBuffer buffer = CreateBuffer(data.Length);
        try
        {
            Check(buffer.Lock(out IntPtr pointer, out _, out _), "IMFMediaBuffer.Lock");
            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
            buffer.Unlock();
            Check(buffer.SetCurrentLength(data.Length), "IMFMediaBuffer.SetCurrentLength");

            IMFSample sample = CreateSample(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(duration);
            return sample;
        }
        finally
        {
            Release(buffer);
        }
    }

    public static byte[]? GetBlob(IMFAttributes attributes, Guid key)
    {
        if (Failed(attributes.GetBlobSize(key, out int size)) || size <= 0)
        {
            return null;
        }

        byte[] blob = new byte[size];
        return Failed(attributes.GetBlob(key, blob, size, out _)) ? null : blob;
    }

    public static string? GetString(IMFAttributes attributes, Guid key) =>
        Failed(attributes.GetAllocatedString(key, out string value, out _)) ? null : value;
}
