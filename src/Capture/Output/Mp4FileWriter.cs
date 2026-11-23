using Capture.Audio;
using Capture.Interop;
using Capture.Video;
using MediaFoundation;
using MediaFoundation.ReadWrite;

namespace Capture.Output;

/// <summary>
/// One MP4 file being written with a Media Foundation sink writer: H.264 video passed straight
/// through (no second encode), and optionally the audio.
/// <para>
/// The MPEG-4 sink cannot store linear PCM, so the 8 kHz PCM is encoded to AAC-LC; the Microsoft
/// AAC encoder only runs at 44.1/48 kHz, so the audio is upsampled to 48 kHz first.
/// </para>
/// <para>
/// Times are on the file's own timeline, which starts at zero. Not thread-safe: the recorder
/// serializes the writes, and calls <see cref="Finish"/> once, after the last one.
/// </para>
/// </summary>
internal sealed class Mp4FileWriter
{
    private const int AacSampleRate = 48000;
    private const int AacBytesPerSecond = 12000; // 96 kbit/s, the lowest rate the Microsoft AAC encoder offers
    private const int AacProfileLevel = 0x29;    // AAC-LC

    private readonly IMFByteStream _file;
    private readonly IMFSinkWriter _writer;
    private readonly int _videoStream;
    private readonly int _audioStream;
    private readonly AudioResampler? _upsampler;
    private readonly PcmHandler _writeAudioBlock;
    private long? _audioOrigin;
    private long _audioSamplesWritten; // at 48 kHz

    private Mp4FileWriter(string path, IMFByteStream file, IMFSinkWriter writer, int videoStream, int audioStream, AudioResampler? upsampler)
    {
        Path = path;
        _file = file;
        _writer = writer;
        _videoStream = videoStream;
        _audioStream = audioStream;
        _upsampler = upsampler;
        _writeAudioBlock = WriteAudioBlock;
    }

    public string Path { get; }

    /// <summary>Length of the video written so far.</summary>
    public long Duration { get; private set; }

    /// <summary>Where the audio written so far ends.</summary>
    public long AudioEnd => (_audioOrigin ?? 0) + _audioSamplesWritten * MediaClock.TicksPerSecond / AacSampleRate;

    /// <summary>
    /// Creates the file, replacing any file of that name, and its folder if needed. The video
    /// stream is described by the encoder's configuration and the SPS/PPS of the first key frame.
    /// </summary>
    public static Mp4FileWriter Create(string path, H264EncoderConfig video, byte[] sequenceHeader, bool withAudio)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        IMFByteStream? file = null;
        IMFSinkWriter? writer = null;
        AudioResampler? upsampler = null;
        IMFAttributes attributes = Mf.CreateAttributes(2);
        try
        {
            // Opened here rather than by the sink writer so that Finish can close it at a known
            // moment (see there). The writer still picks the container from the name's extension.
            Mf.Check(MFExtern.MFCreateFile(MFFileAccessMode.ReadWrite, MFFileOpenMode.DeleteIfExist, MFFileFlags.None, path, out file), $"Create {path}");

            // The input is live: never let the writer hold the capture threads back to pace them.
            Mf.Check(attributes.SetUINT32(MFAttributesClsid.MF_SINK_WRITER_DISABLE_THROTTLING, 1), "Set MF_SINK_WRITER_DISABLE_THROTTLING");
            Mf.Check(MFExtern.MFCreateSinkWriterFromURL(path, file, attributes, out writer), $"Create the MP4 writer for {path}");

            int videoStream = AddVideoStream(writer, video, sequenceHeader);
            int audioStream = withAudio ? AddAudioStream(writer, out upsampler) : -1;
            Mf.Check(writer.BeginWriting(), "Begin writing MP4");
            return new Mp4FileWriter(path, file, writer, videoStream, audioStream, upsampler);
        }
        catch
        {
            upsampler?.Dispose();
            Mf.Release(writer);
            file?.Close();
            Mf.Release(file);
            throw;
        }
        finally
        {
            Mf.Release(attributes);
        }
    }

    public void WriteVideo(ReadOnlySpan<byte> data, long time, long duration, bool isKeyFrame)
    {
        IMFSample sample = Mf.CreateSample(data, time, duration);
        try
        {
            if (isKeyFrame)
            {
                Mf.Check(sample.SetUINT32(MFAttributesClsid.MFSampleExtension_CleanPoint, 1), "Mark key frame");
            }

            Mf.Check(_writer.WriteSample(_videoStream, sample), "Write video sample");
        }
        finally
        {
            Mf.Release(sample);
        }

        Duration = time + duration;
    }

    /// <summary>
    /// Adds 8 kHz PCM to the audio track. The track is one continuous run of samples: the first
    /// block placed in it sets where it starts (<paramref name="time"/>), and every later one
    /// follows straight on from the one before, whatever its own time.
    /// </summary>
    public void WriteAudio(long time, ReadOnlySpan<byte> pcm)
    {
        _audioOrigin ??= time;
        _upsampler!.Process(pcm, _writeAudioBlock);
    }

    /// <summary>
    /// Fills the audio track with silence up to <paramref name="time"/>, so that what is written
    /// next lines up with the video from there; an empty track is filled from the start of the
    /// file. Returns false, writing nothing, if the track already reaches past that time.
    /// </summary>
    public bool PadAudioTo(long time)
    {
        _audioOrigin ??= 0;
        long gap = time - AudioEnd;
        if (gap < 0)
        {
            return false;
        }

        long samples = gap * PcmFormat.SampleRate / MediaClock.TicksPerSecond;
        byte[] silence = new byte[Math.Min(samples, PcmFormat.SampleRate) * 2];
        while (samples > 0)
        {
            int count = (int)Math.Min(samples, silence.Length / 2);
            _upsampler!.Process(silence.AsSpan(0, count * 2), _writeAudioBlock);
            samples -= count;
        }

        return true;
    }

    /// <summary>
    /// Writes the index that makes the file playable, and closes it. Logs the outcome rather than
    /// throwing: it runs in the background, and there is nobody to throw to.
    /// </summary>
    public void Finish()
    {
        string? error = null;
        try
        {
            HResult hr = _writer.Finalize_();
            if (Mf.Failed(hr))
            {
                error = $"{Mf.Describe(hr)}. The file may not be playable.";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            _upsampler?.Dispose();
            Mf.Release(_writer);

            // Left to the writer, the file is closed a little later on a Media Foundation thread,
            // and until then it cannot be moved or deleted and lists as empty. Close it now, so
            // that "Saved" below means the file is ready to be copied or played.
            _file.Close();
            Mf.Release(_file);
        }

        if (error is not null)
        {
            Log.Error($"Could not finalize {Path}: {error}");
            return;
        }

        long size = new FileInfo(Path).Length;
        Log.Info($"Saved {Path} ({TimeSpan.FromTicks(Duration):hh\\:mm\\:ss\\.f}, {size / 1_048_576.0:0.0} MB)");
    }

    private static int AddVideoStream(IMFSinkWriter writer, H264EncoderConfig video, byte[] sequenceHeader)
    {
        IMFMediaType type = Mf.CreateVideoType(MFMediaType.H264, video.Width, video.Height, video.Fps);
        try
        {
            Mf.Check(type.SetUINT32(MFAttributesClsid.MF_MT_AVG_BITRATE, video.BitrateBps), "Set MF_MT_AVG_BITRATE");
            Mf.Check(type.SetUINT32(MFAttributesClsid.MF_MT_MPEG2_PROFILE, (int)video.Profile), "Set MF_MT_MPEG2_PROFILE");
            Mf.Check(type.SetBlob(MFAttributesClsid.MF_MT_MPEG_SEQUENCE_HEADER, sequenceHeader, sequenceHeader.Length), "Set MF_MT_MPEG_SEQUENCE_HEADER");
            Mf.SetYuvColorInfo(type, video.Bt709);

            Mf.Check(writer.AddStream(type, out int stream), "Add MP4 video stream");

            // Input type == stream type: the samples are written as they are, without an encoder.
            Mf.Check(writer.SetInputMediaType(stream, type, null), "Set MP4 video input type");
            return stream;
        }
        finally
        {
            Mf.Release(type);
        }
    }

    private static int AddAudioStream(IMFSinkWriter writer, out AudioResampler upsampler)
    {
        Mf.Check(MFExtern.MFCreateMediaType(out IMFMediaType aacType), "MFCreateMediaType");
        IMFMediaType pcmType = Mf.CreatePcmType(AacSampleRate, PcmFormat.Channels);
        IMFMediaType captureType = Mf.CreatePcmType(PcmFormat.SampleRate, PcmFormat.Channels);
        try
        {
            Mf.Check(aacType.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio), "Set MF_MT_MAJOR_TYPE");
            Mf.Check(aacType.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.AAC), "Set MF_MT_SUBTYPE");
            Mf.Check(aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BITS_PER_SAMPLE, 16), "Set MF_MT_AUDIO_BITS_PER_SAMPLE");
            Mf.Check(aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, AacSampleRate), "Set MF_MT_AUDIO_SAMPLES_PER_SECOND");
            Mf.Check(aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, PcmFormat.Channels), "Set MF_MT_AUDIO_NUM_CHANNELS");
            Mf.Check(aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AacBytesPerSecond), "Set MF_MT_AUDIO_AVG_BYTES_PER_SECOND");
            Mf.Check(aacType.SetUINT32(MFAttributesClsid.MF_MT_AAC_PAYLOAD_TYPE, 0), "Set MF_MT_AAC_PAYLOAD_TYPE");
            Mf.Check(aacType.SetUINT32(MFAttributesClsid.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, AacProfileLevel), "Set MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION");

            Mf.Check(writer.AddStream(aacType, out int stream), "Add MP4 audio stream");
            Mf.Check(writer.SetInputMediaType(stream, pcmType, null), "Set MP4 audio input type");

            upsampler = new AudioResampler(captureType, pcmType);
            return stream;
        }
        finally
        {
            Mf.Release(aacType);
            Mf.Release(pcmType);
            Mf.Release(captureType);
        }
    }

    private void WriteAudioBlock(ReadOnlySpan<byte> pcm)
    {
        int samples = pcm.Length / 2;
        long duration = (long)samples * MediaClock.TicksPerSecond / AacSampleRate;

        IMFSample sample = Mf.CreateSample(pcm, AudioEnd, duration);
        try
        {
            Mf.Check(_writer.WriteSample(_audioStream, sample), "Write audio sample");
        }
        finally
        {
            Mf.Release(sample);
        }

        _audioSamplesWritten += samples;
    }
}
