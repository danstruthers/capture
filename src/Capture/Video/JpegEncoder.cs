using System.Drawing;
using System.Drawing.Imaging;
using Capture.Interop;
using MediaFoundation;

namespace Capture.Video;

/// <summary>One screen picture as a JPEG file.</summary>
internal sealed class JpegFrame
{
    public required byte[] Data { get; init; }

    public required long Timestamp { get; init; }
}

/// <summary>
/// Turns captured BGRX frames into JPEG images: the fallback video method for viewers that cannot
/// take the H.264 stream. Encoding is GDI+'s; scaling, when the output is smaller than the screen,
/// is the Media Foundation video processor's.
/// </summary>
internal sealed class JpegEncoder : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly VideoConverter? _scaler;
    private readonly ImageCodecInfo _codec;
    private readonly EncoderParameters _parameters;
    private readonly MemoryStream _stream = new();

    public JpegEncoder(int sourceWidth, int sourceHeight, int width, int height, int quality)
    {
        _width = width;
        _height = height;
        _codec = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        _parameters = new EncoderParameters(1);
        _parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

        if (width != sourceWidth || height != sourceHeight)
        {
            _scaler = VideoConverter.Scaler(sourceWidth, sourceHeight, width, height);
        }
    }

    /// <summary>Encodes one top-down BGRX frame of the source size. The caller keeps ownership of the sample.</summary>
    public JpegFrame Encode(IMFSample frame, long timestamp)
    {
        IMFSample? scaled = _scaler?.Convert(frame, timestamp, 0);
        try
        {
            IMFSample picture = scaled ?? frame;
            Mf.Check(picture.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "IMFSample.ConvertToContiguousBuffer");
            try
            {
                Mf.Check(buffer.Lock(out IntPtr pixels, out _, out _), "IMFMediaBuffer.Lock");
                try
                {
                    // The bitmap wraps the frame's memory; nothing is copied before compression.
                    using var bitmap = new Bitmap(_width, _height, _width * 4, PixelFormat.Format32bppRgb, pixels);
                    _stream.SetLength(0);
                    bitmap.Save(_stream, _codec, _parameters);
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

            return new JpegFrame { Data = _stream.ToArray(), Timestamp = timestamp };
        }
        finally
        {
            Mf.Release(scaled);
        }
    }

    public void Dispose()
    {
        _scaler?.Dispose();
        _parameters.Dispose();
        _stream.Dispose();
    }
}
