using System.Runtime.InteropServices;
using Capture.Audio;
using Capture.Interop;
using Capture.Video;
using MediaFoundation;
using MediaFoundation.ReadWrite;

namespace Capture.Output;

internal enum RecordingState
{
    Stopped,
    Recording,
    Paused,
}

internal readonly record struct RecordingStatus(RecordingState State, IReadOnlyList<string> Files, long Duration, long Bytes);

/// <summary>A video stream to record: its identity, the configuration of its H.264, and how to ask it for a key frame.</summary>
internal sealed record RecordingSource(string Id, Func<H264EncoderConfig> VideoConfig, Action RequestKeyFrame);

/// <summary>
/// Writes the encoded streams to MP4 files with Media Foundation sink writers: one file per video
/// stream, each with the audio.
/// <para>
/// Video is the already-encoded H.264 passed straight through (no second encode). The MPEG-4 sink
/// cannot store linear PCM, so the 8 kHz PCM is encoded to AAC-LC; the Microsoft AAC encoder only
/// runs at 44.1/48 kHz, so the audio is upsampled to 48 kHz first.
/// </para>
/// <para>
/// Recording can be started, paused, resumed and stopped at any time; the commands apply to all
/// streams together. Each start makes new files, and so does a change of the set of streams while
/// recording. A pause leaves no gap in the files (their timeline simply continues at the resume
/// point). An MP4 file is only playable once it has been finalized, so with a segment length set a
/// new file is started on a key frame every N seconds and a crash costs at most the current segment.
/// </para>
/// </summary>
internal sealed class Mp4Recorder : IDisposable
{
    private const int AacSampleRate = 48000;
    private const int AacBytesPerSecond = 12000; // 96 kbit/s, the lowest rate the Microsoft AAC encoder offers
    private const int AacProfileLevel = 0x29;    // AAC-LC

    private readonly object _lock = new();
    private readonly string _directory;
    private readonly string? _outputPath;
    private readonly long? _segmentLength;
    private readonly bool _overwrite;
    private readonly bool _hasAudio;
    private readonly List<Task> _finalizing = [];

    private IReadOnlyList<RecordingSource> _sources = [];
    private List<Track> _tracks = [];
    private RecordingState _state = RecordingState.Stopped;
    private int _recordingCount;
    private long _recordingBytes;
    private bool _disposed;

    /// <param name="directory">The folder recordings go to; it is created when the first file is.</param>
    /// <param name="outputPath">File to record to (relative to <paramref name="directory"/> unless it is a full path), or null for capture-&lt;date&gt;-&lt;time&gt;.mp4. With several streams, each file name gets the stream's id added: name-screen1.mp4, ...</param>
    public Mp4Recorder(string directory, string? outputPath, TimeSpan? segmentLength, bool overwrite, bool hasAudio)
    {
        _directory = Path.GetFullPath(directory);
        _outputPath = outputPath is null ? null : Path.GetFullPath(outputPath, _directory);
        _segmentLength = segmentLength?.Ticks;
        _overwrite = overwrite;
        _hasAudio = hasAudio;
    }

    /// <summary>The folder the recording files are written to.</summary>
    public string Folder => _outputPath is null ? _directory : Path.GetDirectoryName(_outputPath)!;

    /// <summary>Raised after the recording state or its files change, including when a write error stops the recording.</summary>
    public event Action? StateChanged;

    public RecordingState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>True while frames are being written (or are about to be, waiting for a key frame).</summary>
    public bool WantsMedia => State == RecordingState.Recording;

    public RecordingStatus GetStatus()
    {
        lock (_lock)
        {
            if (_state == RecordingState.Stopped)
            {
                return new RecordingStatus(RecordingState.Stopped, [], 0, _recordingBytes);
            }

            string[] files = [.. _tracks.Select(track => track.Segment?.Path ?? PathForFile(track, Math.Max(1, track.FileCount)))];
            long duration = _tracks.Count == 0 ? 0 : _tracks.Max(track => track.Segment?.Duration ?? 0);
            return new RecordingStatus(_state, files, duration, _recordingBytes);
        }
    }

    /// <summary>
    /// Sets the video streams to record. While a recording is in progress, the files of the previous
    /// streams are finished and the recording carries on in new files for the new streams.
    /// </summary>
    public void SetSources(IReadOnlyList<RecordingSource> sources)
    {
        List<Segment> finished = [];
        bool restarted = false;

        lock (_lock)
        {
            _sources = sources;
            if (_state == RecordingState.Stopped)
            {
                _tracks = [];
            }
            else
            {
                finished = CloseTracksLocked();
                _tracks = CreateTracksLocked(NextRecordingPath());
                restarted = true;
            }
        }

        foreach (Segment segment in finished)
        {
            FinishInBackground(segment);
        }

        if (restarted)
        {
            foreach (RecordingSource source in sources)
            {
                source.RequestKeyFrame();
            }

            StateChanged?.Invoke();
        }
    }

    /// <summary>Starts a new recording (new files), or resumes a paused one. Throws <see cref="InvalidOperationException"/> if the files cannot be created.</summary>
    public void Start()
    {
        IReadOnlyList<RecordingSource> sources;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            switch (_state)
            {
                case RecordingState.Recording:
                    return;
                case RecordingState.Paused:
                    ResumeLocked();
                    break;
                default:
                    _tracks = CreateTracksLocked(NextRecordingPath());
                    _recordingBytes = 0;
                    _state = RecordingState.Recording;
                    break;
            }

            sources = _sources;
        }

        // A file has to begin with a key frame; ask for one rather than wait for the next scheduled one.
        foreach (RecordingSource source in sources)
        {
            source.RequestKeyFrame();
        }

        StateChanged?.Invoke();
    }

    /// <summary>Ends the recording and finalizes its files.</summary>
    public void Stop()
    {
        List<Segment> finished;
        lock (_lock)
        {
            if (_state == RecordingState.Stopped)
            {
                return;
            }

            _state = RecordingState.Stopped;
            finished = CloseTracksLocked();
            _tracks = [];
        }

        foreach (Segment segment in finished)
        {
            FinishInBackground(segment);
        }

        StateChanged?.Invoke();
    }

    /// <summary>Suspends writing; the files stay open.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case RecordingState.Paused:
                    return;
                case RecordingState.Stopped:
                    throw new RefusedException("Recording is stopped, so there is nothing to pause.");
            }

            _state = RecordingState.Paused;
            foreach (Track track in _tracks)
            {
                if (track.Segment is { } segment && !track.ResumePending)
                {
                    track.PausedAt = segment.Origin + segment.PausedTotal + segment.Duration;
                }
            }
        }

        StateChanged?.Invoke();
    }

    /// <summary>Continues a paused recording in the same files.</summary>
    public void Resume()
    {
        IReadOnlyList<RecordingSource> sources;
        lock (_lock)
        {
            switch (_state)
            {
                case RecordingState.Recording:
                    return;
                case RecordingState.Stopped:
                    throw new RefusedException("Recording is stopped. Start it instead of resuming.");
            }

            ResumeLocked();
            sources = _sources;
        }

        foreach (RecordingSource source in sources)
        {
            source.RequestKeyFrame();
        }

        StateChanged?.Invoke();
    }

    public void WriteVideo(string sourceId, EncodedVideoFrame frame)
    {
        Segment? finished = null;
        bool failed = false;

        lock (_lock)
        {
            if (_state != RecordingState.Recording || _tracks.Find(t => t.Source.Id == sourceId) is not { } track)
            {
                return;
            }

            try
            {
                if (frame.IsKeyFrame && frame.SequenceHeader is not null)
                {
                    if (track.Segment is null)
                    {
                        track.Segment = StartSegment(track, frame);
                    }
                    else
                    {
                        if (track.ResumePending)
                        {
                            // Everything between the pause and this key frame is cut out of the file's timeline.
                            track.Segment.PausedTotal += frame.Timestamp - track.PausedAt;
                            track.Segment.ResumedAt = frame.Timestamp;
                            track.Segment.AudioResyncPending = true;
                            track.ResumePending = false;
                        }

                        if (ShouldStartNewFile(track.Segment, frame))
                        {
                            finished = track.Segment;
                            track.Segment = null;
                            track.Segment = StartSegment(track, frame);
                        }
                    }
                }

                if (track.Segment is not { } segment || track.ResumePending)
                {
                    return; // a file, and the part after a pause, has to begin with a key frame
                }

                long time = frame.Timestamp - segment.Origin - segment.PausedTotal;

                if (_segmentLength is { } length && !track.SegmentKeyFrameRequested && time + frame.Duration >= length)
                {
                    track.SegmentKeyFrameRequested = true;
                    track.Source.RequestKeyFrame();
                }

                IMFSample sample = Mf.CreateSample(frame.Data, time, frame.Duration);
                try
                {
                    if (frame.IsKeyFrame)
                    {
                        sample.SetUINT32(MFAttributesClsid.MFSampleExtension_CleanPoint, 1);
                    }

                    Mf.Check(segment.Writer.WriteSample(segment.VideoStream, sample), "Write video sample");
                }
                finally
                {
                    Mf.Release(sample);
                }

                segment.Duration = time + frame.Duration;
                _recordingBytes += frame.Data.Length;
            }
            catch (Exception ex)
            {
                FailLocked(ex);
                failed = true;
            }
        }

        if (finished is not null)
        {
            FinishInBackground(finished);
        }

        if (failed)
        {
            StateChanged?.Invoke();
        }
    }

    public void WriteAudio(AudioFrame frame)
    {
        bool failed = false;

        lock (_lock)
        {
            if (_state == RecordingState.Stopped)
            {
                return;
            }

            try
            {
                foreach (Track track in _tracks)
                {
                    WriteAudioLocked(track, frame);
                }
            }
            catch (Exception ex)
            {
                FailLocked(ex);
                failed = true;
            }
        }

        if (failed)
        {
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        List<Segment> last;
        Task[] pending;

        lock (_lock)
        {
            _disposed = true;
            _state = RecordingState.Stopped;
            last = CloseTracksLocked();
            _tracks = [];
            pending = [.. _finalizing];
            _finalizing.Clear();
        }

        foreach (Segment segment in last)
        {
            Finish(segment);
        }

        Task.WaitAll(pending);
    }

    private static void Finish(Segment segment)
    {
        string? error = null;
        try
        {
            HResult hr = segment.Writer.Finalize_();
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
            segment.Upsampler?.Dispose();
            Mf.Release(segment.Writer);

            // Left to the writer, the file is closed a little later on a Media Foundation thread,
            // and until then it cannot be moved or deleted and lists as empty. Close it now, so
            // that "Saved" below means the file is ready to be copied or played.
            segment.File.Close();
            Mf.Release(segment.File);
        }

        if (error is not null)
        {
            Log.Error($"Could not finalize {segment.Path}: {error}");
            return;
        }

        long size = new FileInfo(segment.Path).Length;
        Log.Info($"Saved {segment.Path} ({TimeSpan.FromTicks(segment.Duration):hh\\:mm\\:ss\\.f}, {size / 1_048_576.0:0.0} MB)");
    }

    private static int AddAudioStream(IMFSinkWriter writer, out AudioResampler upsampler)
    {
        Mf.Check(MFExtern.MFCreateMediaType(out IMFMediaType aacType), "MFCreateMediaType");
        IMFMediaType pcmType = Mf.CreatePcmType(AacSampleRate, PcmFormat.Channels);
        IMFMediaType captureType = Mf.CreatePcmType(PcmFormat.SampleRate, PcmFormat.Channels);
        try
        {
            aacType.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio);
            aacType.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.AAC);
            aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, AacSampleRate);
            aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, PcmFormat.Channels);
            aacType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AacBytesPerSecond);
            aacType.SetUINT32(MFAttributesClsid.MF_MT_AAC_PAYLOAD_TYPE, 0);
            aacType.SetUINT32(MFAttributesClsid.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, AacProfileLevel);

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

    private static string Stem(string path) => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));

    /// <summary>The name of a stream's file within a recording: the recording's name, plus the stream's id when there are several streams.</summary>
    private string TrackPath(string recordingPath, RecordingSource source) =>
        _sources.Count <= 1 ? recordingPath : $"{Stem(recordingPath)}-{source.Id}{Path.GetExtension(recordingPath)}";

    /// <summary>The n-th file of a stream: name-001.mp4, ... when segmenting; otherwise name.mp4, then name-part2.mp4 if the encoder configuration changes.</summary>
    private string PathForFile(Track track, int number)
    {
        string extension = Path.GetExtension(track.BasePath);
        if (_segmentLength is not null)
        {
            return $"{Stem(track.BasePath)}-{number:000}{extension}";
        }

        return number == 1 ? track.BasePath : $"{Stem(track.BasePath)}-part{number}{extension}";
    }

    private bool AnyFileExists(string recordingPath) =>
        _sources.Any(source => File.Exists(PathForFile(new Track(source, TrackPath(recordingPath, source)), 1)));

    /// <summary>
    /// Picks the name of a new recording: the configured name the first time, then numbered or
    /// timestamped names. A recording only counts once its name is settled, so that a refused start
    /// (the name is taken) does not move later recordings on to the next number.
    /// </summary>
    private string NextRecordingPath()
    {
        string path = ChooseRecordingPath(_recordingCount + 1);
        _recordingCount++;
        return path;
    }

    private string ChooseRecordingPath(int recordingNumber)
    {
        if (_outputPath is null)
        {
            string stem = Path.Combine(_directory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}");
            string path = $"{stem}.mp4";
            for (int n = 2; AnyFileExists(path); n++)
            {
                path = $"{stem}-{n}.mp4";
            }

            return path;
        }

        if (recordingNumber == 1)
        {
            if (!_overwrite && AnyFileExists(_outputPath))
            {
                throw new RefusedException($"A recording called {Path.GetFileName(_outputPath)} already exists in {Path.GetDirectoryName(_outputPath)}. Choose another --output name or pass --overwrite.");
            }

            return _outputPath;
        }

        string extension = Path.GetExtension(_outputPath);
        for (int n = recordingNumber; ; n++)
        {
            string path = $"{Stem(_outputPath)}-{n}{extension}";
            if (_overwrite || !AnyFileExists(path))
            {
                return path;
            }
        }
    }

    private List<Track> CreateTracksLocked(string recordingPath) =>
        [.. _sources.Select(source => new Track(source, TrackPath(recordingPath, source)))];

    /// <summary>Detaches every open file from its track; the caller finalizes them.</summary>
    private List<Segment> CloseTracksLocked()
    {
        var segments = new List<Segment>();
        foreach (Track track in _tracks)
        {
            if (track.Segment is not null)
            {
                segments.Add(track.Segment);
                track.Segment = null;
            }
        }

        return segments;
    }

    private void ResumeLocked()
    {
        _state = RecordingState.Recording;

        // With a file open, writing picks up again at the next key frame; without one, the file simply starts there.
        foreach (Track track in _tracks)
        {
            track.ResumePending = track.Segment is not null;
        }
    }

    private void WriteAudioLocked(Track track, AudioFrame frame)
    {
        if (track.Segment is not { Upsampler: not null } segment)
        {
            return;
        }

        // Audio reaches the recorder a little later than the video it belongs with, so a frame is
        // judged by when it was captured, not by the state right now: what was captured before a
        // pause began is still written, what was captured during the pause is not.
        bool suspended = _state == RecordingState.Paused || track.ResumePending;
        if (suspended ? frame.Timestamp >= track.PausedAt : frame.Timestamp < segment.ResumedAt)
        {
            return;
        }

        long time = frame.Timestamp - segment.Origin - segment.PausedTotal;
        if (time < 0)
        {
            return; // captured before this file's first video frame
        }

        if (segment.AudioSamplesWritten == 0 && !segment.AudioResyncPending)
        {
            segment.AudioOrigin = time;
        }
        else if (segment.AudioResyncPending)
        {
            long written = segment.AudioOrigin + segment.AudioSamplesWritten * MediaClock.TicksPerSecond / AacSampleRate;
            if (time < written)
            {
                return; // from before the pause point; already covered
            }

            // The audio track is one continuous run of samples, so bridge any small difference
            // between where it stopped and where it resumes with silence to keep it in step with the video.
            WriteSilence(segment, time - written);
        }

        segment.AudioResyncPending = false;
        WriteAudioSamples(segment, MemoryMarshal.AsBytes(frame.Samples.AsSpan()));
    }

    private void WriteSilence(Segment segment, long duration)
    {
        long samples = duration * PcmFormat.SampleRate / MediaClock.TicksPerSecond;
        if (samples <= 0)
        {
            return;
        }

        byte[] silence = new byte[Math.Min(samples, PcmFormat.SampleRate) * 2];
        while (samples > 0)
        {
            int count = (int)Math.Min(samples, silence.Length / 2);
            WriteAudioSamples(segment, silence.AsSpan(0, count * 2));
            samples -= count;
        }
    }

    private void WriteAudioSamples(Segment segment, ReadOnlySpan<byte> pcm) =>
        segment.Upsampler!.Process(pcm, upsampled => WriteAudioBlock(segment, upsampled));

    private static void WriteAudioBlock(Segment segment, ReadOnlySpan<byte> pcm)
    {
        int samples = pcm.Length / 2;
        long time = segment.AudioOrigin + segment.AudioSamplesWritten * MediaClock.TicksPerSecond / AacSampleRate;
        long duration = (long)samples * MediaClock.TicksPerSecond / AacSampleRate;

        IMFSample sample = Mf.CreateSample(pcm, time, duration);
        try
        {
            Mf.Check(segment.Writer.WriteSample(segment.AudioStream, sample), "Write audio sample");
        }
        finally
        {
            Mf.Release(sample);
        }

        segment.AudioSamplesWritten += samples;
    }

    private bool ShouldStartNewFile(Segment segment, EncodedVideoFrame keyFrame)
    {
        // A different SPS/PPS means a different encoder configuration (a new frame rate, or a fall
        // back from hardware to software); one MP4 video track cannot hold both.
        if (!keyFrame.SequenceHeader.AsSpan().SequenceEqual(segment.SequenceHeader))
        {
            return true;
        }

        return _segmentLength is { } length && keyFrame.Timestamp - segment.Origin - segment.PausedTotal >= length;
    }

    private Segment StartSegment(Track track, EncodedVideoFrame keyFrame)
    {
        string path = PathForFile(track, ++track.FileCount);
        track.SegmentKeyFrameRequested = false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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
            attributes.SetUINT32(MFAttributesClsid.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            Mf.Check(MFExtern.MFCreateSinkWriterFromURL(path, file, attributes, out writer), $"Create the MP4 writer for {path}");

            int videoStream = AddVideoStream(writer, track.Source.VideoConfig(), keyFrame.SequenceHeader!);
            int audioStream = -1;
            if (_hasAudio)
            {
                audioStream = AddAudioStream(writer, out upsampler);
            }

            Mf.Check(writer.BeginWriting(), "Begin writing MP4");
            Log.Info($"Recording to {path}");

            return new Segment
            {
                Path = path,
                File = file,
                Writer = writer,
                Origin = keyFrame.Timestamp,
                SequenceHeader = keyFrame.SequenceHeader!,
                VideoStream = videoStream,
                AudioStream = audioStream,
                Upsampler = upsampler,
            };
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

    private void FinishInBackground(Segment segment)
    {
        // Finalizing flushes the AAC encoder and writes the index; keep that off the encode threads.
        Task task = Task.Run(() => Finish(segment));
        lock (_lock)
        {
            _finalizing.RemoveAll(t => t.IsCompleted);
            _finalizing.Add(task);
        }
    }

    /// <summary>Stops recording after a write error (for example a full disk) without stopping the capture.</summary>
    private void FailLocked(Exception exception)
    {
        Log.Error($"Recording stopped: {exception.Message}");
        _state = RecordingState.Stopped;

        foreach (Segment segment in CloseTracksLocked())
        {
            _finalizing.Add(Task.Run(() => Finish(segment)));
        }

        _tracks = [];
    }

    /// <summary>The recording of one video stream: its current file and its pause bookkeeping.</summary>
    private sealed class Track(RecordingSource source, string basePath)
    {
        public RecordingSource Source { get; } = source;

        public string BasePath { get; } = basePath;

        public Segment? Segment { get; set; }

        public int FileCount { get; set; }

        public bool SegmentKeyFrameRequested { get; set; }

        /// <summary>Set by a resume: writing restarts at the next key frame.</summary>
        public bool ResumePending { get; set; }

        /// <summary>Session-clock time at which the last pause began.</summary>
        public long PausedAt { get; set; }
    }

    private sealed class Segment
    {
        public required string Path { get; init; }

        public required IMFByteStream File { get; init; }

        public required IMFSinkWriter Writer { get; init; }

        /// <summary>Session-clock time of this file's first video frame; its timeline starts at zero there.</summary>
        public required long Origin { get; init; }

        public required byte[] SequenceHeader { get; init; }

        public required int VideoStream { get; init; }

        public required int AudioStream { get; init; }

        public AudioResampler? Upsampler { get; init; }

        /// <summary>Total time cut out of this file by pauses.</summary>
        public long PausedTotal { get; set; }

        /// <summary>Session-clock time at which writing last resumed after a pause.</summary>
        public long ResumedAt { get; set; }

        /// <summary>Set after a pause: the next audio frame has to be lined up with the video again.</summary>
        public bool AudioResyncPending { get; set; }

        public long AudioOrigin { get; set; }

        public long AudioSamplesWritten { get; set; }

        /// <summary>Length of the video written so far.</summary>
        public long Duration { get; set; }
    }
}
