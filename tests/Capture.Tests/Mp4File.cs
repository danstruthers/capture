using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;
using MediaFoundation.ReadWrite;

namespace Capture.Tests;

/// <summary>What a recorded MP4 file contains, read back by decoding it with a Media Foundation source reader.</summary>
internal sealed unsafe class Mp4File
{
    private const int VideoStream = (int)MF_SOURCE_READER.FirstVideoStream;
    private const int AudioStream = (int)MF_SOURCE_READER.FirstAudioStream;

    private byte[] _firstFrame = [];
    private int _firstFrameStride;
    private int _decodedStride;

    private Mp4File()
    {
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int VideoFrames { get; private set; }

    public double VideoSeconds { get; private set; }

    public bool HasAudio { get; private set; }

    public int AudioSampleRate { get; private set; }

    public double AudioSeconds { get; private set; }

    /// <summary>Root mean square of the decoded audio on a 16-bit scale.</summary>
    public double AudioRms { get; private set; }

    /// <summary>The blue, green and red values of a pixel of the first decoded frame; (0, 0) is the top-left corner.</summary>
    public (int Blue, int Green, int Red) FirstFramePixel(int x, int y)
    {
        int offset = y * _firstFrameStride + x * 4;
        return (_firstFrame[offset], _firstFrame[offset + 1], _firstFrame[offset + 2]);
    }

    public static Mp4File Read(string path)
    {
        var file = new Mp4File();

        IMFAttributes attributes = Mf.CreateAttributes(1);
        attributes.SetUINT32(MFAttributesClsid.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1); // lets the reader hand out RGB
        Mf.Check(MFExtern.MFCreateSourceReaderFromURL(path, attributes, out IMFSourceReader reader), $"Open {path}");
        try
        {
            file.ReadVideo(reader);
            file.ReadAudio(reader);
        }
        finally
        {
            Mf.Release(reader);
            Mf.Release(attributes);
        }

        return file;
    }

    private void ReadVideo(IMFSourceReader reader)
    {
        Mf.Check(MFExtern.MFCreateMediaType(out IMFMediaType rgb), "MFCreateMediaType");
        rgb.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Video);
        rgb.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.RGB32);
        Mf.Check(reader.SetCurrentMediaType(VideoStream, null, rgb), "Decode video to RGB32");
        Mf.Release(rgb);

        Mf.Check(reader.GetCurrentMediaType(VideoStream, out IMFMediaType current), "Read video format");
        MFExtern.MFGetAttributeSize(current, MFAttributesClsid.MF_MT_FRAME_SIZE, out int width, out int height);

        // The stride is how to get from one row to the next; negative means the buffer starts with the bottom row.
        if (Mf.Failed(current.GetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, out int stride)))
        {
            Mf.Check(MFExtern.MFGetStrideForBitmapInfoHeader(22 /* D3DFMT_X8R8G8B8, i.e. RGB32 */, width, out stride), "MFGetStrideForBitmapInfoHeader");
        }

        Mf.Release(current);
        Width = width;
        Height = height;
        _decodedStride = stride;

        long end = 0;
        while (true)
        {
            Mf.Check(reader.ReadSample(VideoStream, MF_SOURCE_READER_CONTROL_FLAG.None, out _, out MF_SOURCE_READER_FLAG flags, out long time, out IMFSample sample), "Read video sample");
            if (sample is not null)
            {
                if (VideoFrames == 0)
                {
                    CopyFirstFrame(sample);
                }

                sample.GetSampleDuration(out long duration);
                end = Math.Max(end, time + duration);
                VideoFrames++;
                Mf.Release(sample);
            }

            if ((flags & MF_SOURCE_READER_FLAG.EndOfStream) != 0)
            {
                break;
            }
        }

        VideoSeconds = end / (double)MediaClock.TicksPerSecond;
    }

    private void CopyFirstFrame(IMFSample sample)
    {
        Mf.Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "ConvertToContiguousBuffer");
        try
        {
            _firstFrameStride = Width * 4;
            _firstFrame = new byte[_firstFrameStride * Height];

            Mf.Check(buffer.Lock(out IntPtr memory, out _, out _), "Lock");
            try
            {
                int rowBytes = Math.Abs(_decodedStride);
                IntPtr topRow = _decodedStride < 0 ? memory + (Height - 1) * rowBytes : memory;
                for (int y = 0; y < Height; y++)
                {
                    Marshal.Copy(topRow + y * _decodedStride, _firstFrame, y * _firstFrameStride, _firstFrameStride);
                }
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Mf.Release(buffer);
        }
    }

    private void ReadAudio(IMFSourceReader reader)
    {
        Mf.Check(MFExtern.MFCreateMediaType(out IMFMediaType pcm), "MFCreateMediaType");
        pcm.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio);
        pcm.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.PCM);
        HResult hr = reader.SetCurrentMediaType(AudioStream, null, pcm);
        Mf.Release(pcm);
        if (Mf.Failed(hr))
        {
            return; // no audio stream
        }

        HasAudio = true;
        Mf.Check(reader.GetCurrentMediaType(AudioStream, out IMFMediaType current), "Read audio format");
        current.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, out int sampleRate);
        current.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, out int channels);
        Mf.Release(current);
        AudioSampleRate = sampleRate;

        long sampleCount = 0;
        double sumOfSquares = 0;
        while (true)
        {
            Mf.Check(reader.ReadSample(AudioStream, MF_SOURCE_READER_CONTROL_FLAG.None, out _, out MF_SOURCE_READER_FLAG flags, out _, out IMFSample sample), "Read audio sample");
            if (sample is not null)
            {
                Mf.Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "ConvertToContiguousBuffer");
                Mf.Check(buffer.Lock(out IntPtr pointer, out _, out int length), "Lock");
                var samples = new ReadOnlySpan<short>((void*)pointer, length / 2);
                foreach (short value in samples)
                {
                    sumOfSquares += (double)value * value;
                }

                sampleCount += samples.Length;
                buffer.Unlock();
                Mf.Release(buffer);
                Mf.Release(sample);
            }

            if ((flags & MF_SOURCE_READER_FLAG.EndOfStream) != 0)
            {
                break;
            }
        }

        AudioSeconds = sampleCount / (double)(sampleRate * channels);
        AudioRms = sampleCount > 0 ? Math.Sqrt(sumOfSquares / sampleCount) : 0;
    }
}
