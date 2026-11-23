using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;
using MediaFoundation.Transform;

namespace Capture.Video;

/// <summary>
/// Converts captured BGRX frames to another format and/or size with the Media Foundation Video
/// Processor MFT: to NV12 for the H.264 encoder, or to a smaller BGRX picture for JPEG encoding.
/// </summary>
internal sealed class VideoConverter : IDisposable
{
    private readonly IMFTransform _processor;
    private readonly int _outputBufferSize;
    private bool _disposed;

    /// <param name="outputSubtype"><see cref="MFMediaType.NV12"/> or <see cref="MFMediaType.RGB32"/>.</param>
    public VideoConverter(int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps, Guid outputSubtype, bool bt709)
    {
        bool rgbOutput = outputSubtype == MFMediaType.RGB32;
        _processor = (IMFTransform)new VideoProcessorMFT();

        try
        {
            if (!Mf.Failed(_processor.GetAttributes(out IMFAttributes attributes)) && attributes is not null)
            {
                // No frame-rate conversion: exactly one frame out for each frame in.
                attributes.SetUINT32(MFAttributesClsid.MF_XVP_DISABLE_FRC, 1);
                Mf.Release(attributes);
            }

            IMFMediaType inputType = Mf.CreateVideoType(MFMediaType.RGB32, inputWidth, inputHeight, fps);
            try
            {
                // A positive stride tells the processor the rows are top-down, as the capturer produces them.
                inputType.SetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, inputWidth * 4);
                inputType.SetUINT32(MFAttributesClsid.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                inputType.SetUINT32(MFAttributesClsid.MF_MT_FIXED_SIZE_SAMPLES, 1);
                inputType.SetUINT32(MFAttributesClsid.MF_MT_SAMPLE_SIZE, inputWidth * inputHeight * 4);
                Mf.Check(_processor.SetInputType(0, inputType, MFTSetTypeFlags.None), "Video processor SetInputType(RGB32)");
            }
            finally
            {
                Mf.Release(inputType);
            }

            IMFMediaType outputType = Mf.CreateVideoType(outputSubtype, outputWidth, outputHeight, fps);
            try
            {
                if (rgbOutput)
                {
                    outputType.SetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, outputWidth * 4);
                }
                else
                {
                    Mf.SetYuvColorInfo(outputType, bt709);
                }

                Mf.Check(_processor.SetOutputType(0, outputType, MFTSetTypeFlags.None), "Video processor SetOutputType");
            }
            finally
            {
                Mf.Release(outputType);
            }

            Mf.Check(_processor.GetOutputStreamInfo(0, out MFTOutputStreamInfo info), "Video processor GetOutputStreamInfo");
            int minimumSize = rgbOutput ? outputWidth * outputHeight * 4 : outputWidth * outputHeight * 3 / 2;
            _outputBufferSize = Math.Max(info.cbSize, minimumSize);

            Mf.Check(_processor.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero), "Video processor begin streaming");
            Mf.Check(_processor.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero), "Video processor start of stream");
        }
        catch
        {
            Mf.Release(_processor);
            throw;
        }
    }

    /// <summary>Converts one frame. The caller keeps ownership of <paramref name="input"/> and owns the returned sample.</summary>
    public IMFSample Convert(IMFSample input, long timestamp, long duration)
    {
        Mf.Check(_processor.ProcessInput(0, input, 0), "Video processor ProcessInput");

        IMFMediaBuffer buffer = Mf.CreateBuffer(_outputBufferSize);
        IMFSample output = Mf.CreateSample(buffer);
        Mf.Release(buffer);

        try
        {
            Mf.Check(Mf.ProcessOutput(_processor, output, out _), "Video processor ProcessOutput");
            output.SetSampleTime(timestamp);
            output.SetSampleDuration(duration);
            return output;
        }
        catch
        {
            Mf.Release(output);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _processor.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero);
        _processor.ProcessMessage(MFTMessageType.NotifyEndStreaming, IntPtr.Zero);
        Mf.Release(_processor);
    }
}
