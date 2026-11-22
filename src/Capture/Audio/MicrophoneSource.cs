using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;
using MediaFoundation.ReadWrite;

namespace Capture.Audio;

/// <summary>Something that records audio and places it, converted to <see cref="PcmFormat"/>, on a timeline.</summary>
internal interface IAudioSource : IDisposable
{
    string Name { get; }

    AudioTimeline Timeline { get; }

    /// <summary>Set if the source stopped by itself (device unplugged, format changed, ...).</summary>
    Exception? Failure { get; }
}

/// <summary>
/// Records from a recording device (microphone, line in) through a Media Foundation source reader
/// and resamples to 8 kHz mono 16-bit PCM, whatever format the device itself runs at.
/// </summary>
internal sealed class MicrophoneSource : IAudioSource
{
    private const int AudioStream = (int)MF_SOURCE_READER.FirstAudioStream;

    private readonly long _systemTimeOffset;
    private readonly IMFMediaSource _source;
    private readonly IMFSourceReader _reader;
    private readonly AudioResampler _resampler;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private bool _disposed;

    public MicrophoneSource(string? deviceSelector, MediaClock clock)
    {
        // Device timestamps are on the Media Foundation system clock; this moves them to the session clock.
        _systemTimeOffset = MFExtern.MFGetSystemTime() - clock.Now;
        _source = AudioDevices.Open(deviceSelector, out string deviceName);

        try
        {
            Mf.Check(MFExtern.MFCreateSourceReaderFromMediaSource(_source, null, out _reader), "Create audio source reader");
            Mf.Check(_reader.SetStreamSelection((int)MF_SOURCE_READER.AllStreams, false), "Deselect streams");
            Mf.Check(_reader.SetStreamSelection(AudioStream, true), "Select audio stream");

            // Take the device's own format and convert it explicitly, so the conversion to 8 kHz
            // (including the anti-aliasing filter) is the resampler MFT's and not driver-dependent.
            Mf.Check(_reader.GetCurrentMediaType(AudioStream, out IMFMediaType deviceType), "Read audio device format");
            IMFMediaType outputType = Mf.CreatePcmType(PcmFormat.SampleRate, PcmFormat.Channels);
            try
            {
                Name = $"{deviceName} ({Describe(deviceType)})";
                _resampler = new AudioResampler(deviceType, outputType);
            }
            finally
            {
                Mf.Release(deviceType);
                Mf.Release(outputType);
            }
        }
        catch
        {
            Mf.Release(_reader);
            _source.Shutdown();
            Mf.Release(_source);
            throw;
        }

        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "microphone-capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public string Name { get; }

    public AudioTimeline Timeline { get; } = new();

    public Exception? Failure { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        if (!_thread.Join(TimeSpan.FromSeconds(1)))
        {
            // The reader is blocked waiting for a device that stopped delivering; shutting the source down unblocks it.
            _source.Shutdown();
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _source.Shutdown();
        _resampler.Dispose();
        Mf.Release(_reader);
        Mf.Release(_source);
    }

    private static string Describe(IMFMediaType type)
    {
        type.GetGUID(MFAttributesClsid.MF_MT_SUBTYPE, out Guid subtype);
        type.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, out int rate);
        type.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, out int channels);
        type.GetUINT32(MFAttributesClsid.MF_MT_AUDIO_BITS_PER_SAMPLE, out int bits);
        string format = subtype == MFMediaType.Float ? "float" : subtype == MFMediaType.PCM ? "PCM" : subtype.ToString();
        return $"{rate} Hz, {channels} ch, {bits}-bit {format}";
    }

    private void ReadLoop()
    {
        try
        {
            while (!_stopping)
            {
                HResult hr = _reader.ReadSample(AudioStream, MF_SOURCE_READER_CONTROL_FLAG.None, out _, out MF_SOURCE_READER_FLAG flags, out long time, out IMFSample sample);
                if (_stopping)
                {
                    Mf.Release(sample);
                    return;
                }

                Mf.Check(hr, "Read audio");

                try
                {
                    if ((flags & (MF_SOURCE_READER_FLAG.Error | MF_SOURCE_READER_FLAG.EndOfStream)) != 0)
                    {
                        throw new InvalidOperationException("The recording device stopped delivering data.");
                    }

                    if ((flags & (MF_SOURCE_READER_FLAG.NativeMediaTypeChanged | MF_SOURCE_READER_FLAG.CurrentMediaTypeChanged)) != 0)
                    {
                        throw new InvalidOperationException("The recording device changed its format.");
                    }

                    if (sample is null)
                    {
                        continue;
                    }

                    // The resampler may hand its output back in several pieces; they follow one another in time.
                    long captureTime = time - _systemTimeOffset;
                    _resampler.Process(sample, pcm =>
                    {
                        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
                        Timeline.Write(captureTime, samples);
                        captureTime += (long)samples.Length * MediaClock.TicksPerSecond / PcmFormat.SampleRate;
                    });
                }
                finally
                {
                    Mf.Release(sample);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopping)
            {
                Failure = ex;
            }
        }
    }
}
