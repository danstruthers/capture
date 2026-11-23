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
    // Frame-rate conversion is off (one frame out for each frame in), so the frame rate the media
    // types carry is only nominal: the frames keep the times and durations they are given.
    private const int NominalFps = 30;

    private readonly IMFTransform _processor;
    private readonly int _outputBufferSize;
    private bool _disposed;

    /// <param name="outputSubtype"><see cref="MFMediaType.NV12"/> or <see cref="MFMediaType.RGB32"/>.</param>
    /// <param name="bt709">For NV12: BT.709 colours rather than BT.601.</param>
    private VideoConverter(int inputWidth, int inputHeight, int outputWidth, int outputHeight, Guid outputSubtype, bool bt709)
    {
        bool rgbOutput = outputSubtype == MFMediaType.RGB32;
        _processor = (IMFTransform)new VideoProcessorMFT();

        try
        {
            if (!Mf.Failed(_processor.GetAttributes(out IMFAttributes attributes)) && attributes is not null)
            {
                Mf.Check(attributes.SetUINT32(MFAttributesClsid.MF_XVP_DISABLE_FRC, 1), "Set MF_XVP_DISABLE_FRC");
                Mf.Release(attributes);
            }

            IMFMediaType inputType = Mf.CreateVideoType(MFMediaType.RGB32, inputWidth, inputHeight, NominalFps);
            try
            {
                // A positive stride tells the processor the rows are top-down, as the capturer produces them.
                Mf.Check(inputType.SetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, inputWidth * 4), "Set MF_MT_DEFAULT_STRIDE");
                Mf.Check(inputType.SetUINT32(MFAttributesClsid.MF_MT_ALL_SAMPLES_INDEPENDENT, 1), "Set MF_MT_ALL_SAMPLES_INDEPENDENT");
                Mf.Check(inputType.SetUINT32(MFAttributesClsid.MF_MT_FIXED_SIZE_SAMPLES, 1), "Set MF_MT_FIXED_SIZE_SAMPLES");
                Mf.Check(inputType.SetUINT32(MFAttributesClsid.MF_MT_SAMPLE_SIZE, inputWidth * inputHeight * 4), "Set MF_MT_SAMPLE_SIZE");
                Mf.Check(_processor.SetInputType(0, inputType, MFTSetTypeFlags.None), "Video processor SetInputType(RGB32)");
            }
            finally
            {
                Mf.Release(inputType);
            }

            IMFMediaType outputType = Mf.CreateVideoType(outputSubtype, outputWidth, outputHeight, NominalFps);
            try
            {
                if (rgbOutput)
                {
                    Mf.Check(outputType.SetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, outputWidth * 4), "Set MF_MT_DEFAULT_STRIDE");
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

    /// <summary>To NV12 of the given size, for the H.264 encoder.</summary>
    /// <param name="bt709">BT.709 colours rather than BT.601; it has to match what the encoder is told.</param>
    public static VideoConverter ToNv12(int inputWidth, int inputHeight, int outputWidth, int outputHeight, bool bt709) =>
        new(inputWidth, inputHeight, outputWidth, outputHeight, MFMediaType.NV12, bt709);

    /// <summary>To a BGRX picture of another size, for JPEG encoding.</summary>
    public static VideoConverter Scaler(int inputWidth, int inputHeight, int outputWidth, int outputHeight) =>
        new(inputWidth, inputHeight, outputWidth, outputHeight, MFMediaType.RGB32, bt709: false);

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
