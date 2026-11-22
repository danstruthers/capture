using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;
using MediaFoundation.Transform;

namespace Capture.Audio;

internal delegate void PcmHandler(ReadOnlySpan<byte> pcm);

/// <summary>
/// Converts uncompressed audio between sample rates, channel counts and sample formats with the
/// Media Foundation audio resampler MFT.
/// </summary>
internal sealed unsafe class AudioResampler : IDisposable
{
    private readonly IMFTransform _transform;
    private readonly IMFMediaBuffer _outputBuffer;
    private readonly int _inputBytesPerSecond;
    private long _inputBytes;
    private bool _disposed;

    /// <summary>The media types are read during construction and remain owned by the caller.</summary>
    public AudioResampler(IMFMediaType inputType, IMFMediaType outputType)
    {
        _transform = (IMFTransform)new AudioResamplerMediaObject();
        try
        {
            Mf.Check(_transform.SetInputType(0, inputType, MFTSetTypeFlags.None), "Resampler SetInputType");
            Mf.Check(_transform.SetOutputType(0, outputType, MFTSetTypeFlags.None), "Resampler SetOutputType");

            Mf.Check(inputType.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, out _inputBytesPerSecond), "Read input byte rate");
            Mf.Check(outputType.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, out int outputBytesPerSecond), "Read output byte rate");
            Mf.Check(outputType.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_BLOCK_ALIGNMENT, out int blockAlign), "Read output block alignment");

            // Room for a quarter of a second per call; ProcessOutput is simply called again if there is more.
            int size = Math.Max(blockAlign, outputBytesPerSecond / 4 / blockAlign * blockAlign);
            _outputBuffer = Mf.CreateBuffer(size);

            Mf.Check(_transform.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero), "Resampler begin streaming");
            Mf.Check(_transform.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero), "Resampler start of stream");
        }
        catch
        {
            Mf.Release(_outputBuffer);
            Mf.Release(_transform);
            throw;
        }
    }

    /// <summary>Resamples one input sample; converted audio is passed to <paramref name="handler"/> as it is produced.</summary>
    public void Process(IMFSample input, PcmHandler handler)
    {
        Mf.Check(_transform.ProcessInput(0, input, 0), "Resampler ProcessInput");

        while (true)
        {
            Mf.Check(_outputBuffer.SetCurrentLength(0), "Reset resampler output buffer");
            IMFSample output = Mf.CreateSample(_outputBuffer);
            IntPtr outputPointer = Marshal.GetComInterfaceForObject(output, typeof(IMFSample));
            try
            {
                var buffers = new MFTOutputDataBuffer[1];
                buffers[0].pSample = outputPointer;

                HResult hr = _transform.ProcessOutput(MFTProcessOutputFlags.None, 1, buffers, out _);
                Mf.Release(buffers[0].pEvents);
                if (hr == HResult.MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    return;
                }

                Mf.Check(hr, "Resampler ProcessOutput");

                Mf.Check(_outputBuffer.Lock(out IntPtr pointer, out _, out int length), "IMFMediaBuffer.Lock");
                try
                {
                    if (length == 0)
                    {
                        return;
                    }

                    handler(new ReadOnlySpan<byte>((void*)pointer, length));
                }
                finally
                {
                    _outputBuffer.Unlock();
                }
            }
            finally
            {
                Marshal.Release(outputPointer);
                Mf.Release(output);
            }
        }
    }

    /// <summary>Resamples a block of raw audio in the input format.</summary>
    public void Process(ReadOnlySpan<byte> input, PcmHandler handler)
    {
        long time = _inputBytes * MediaClock.TicksPerSecond / _inputBytesPerSecond;
        long duration = (long)input.Length * MediaClock.TicksPerSecond / _inputBytesPerSecond;
        _inputBytes += input.Length;

        IMFSample sample = Mf.CreateSample(input, time, duration);
        try
        {
            Process(sample, handler);
        }
        finally
        {
            Mf.Release(sample);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transform.ProcessMessage(MFTMessageType.NotifyEndStreaming, IntPtr.Zero);
        Mf.Release(_outputBuffer);
        Mf.Release(_transform);
    }
}
