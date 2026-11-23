using System.Runtime.InteropServices;
using Capture.Audio;
using Capture.Video;

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
/// Records the encoded streams to MP4 files: one file per video stream, each with the audio
/// (see <see cref="Mp4FileWriter"/> for what goes into a file, <see cref="RecordingNames"/> for
/// what the files are called).
/// <para>
/// Recording can be started, paused, resumed and stopped at any time; the commands apply to all
/// streams together. Each start makes new files, and so does a change of the set of streams while
/// recording. A pause leaves no gap in the files (their timeline simply continues at the resume
/// point). An MP4 file is only playable once it has been finalized, so with a segment length set a
/// new file is started on a key frame every N seconds and a crash costs at most the current segment.
/// </para>
/// <para>
/// Threads: video arrives on each stream's encoder thread, audio on the mixer thread, and commands
/// on the session's threads; <c>_lock</c> serializes them all. Files are finalized in the
/// background, off the encoder threads, since that flushes the AAC encoder and writes the index.
/// </para>
/// </summary>
internal sealed class Mp4Recorder : IDisposable
{
    private readonly object _lock = new();
    private readonly RecordingNames _names;
    private readonly long? _segmentLength;
    private readonly bool _hasAudio;
    private readonly List<Task> _finalizing = [];

    private IReadOnlyList<RecordingSource> _sources = [];
    private List<Track> _tracks = [];
    private RecordingState _state = RecordingState.Stopped;
    private int _recordingCount;
    private long _recordingBytes;
    private bool _disposed;

    /// <param name="directory">The folder recordings go to; it is created when the first file is.</param>
    /// <param name="outputPath">File to record to (relative to <paramref name="directory"/> unless it is a full path), or null for capture-&lt;date&gt;-&lt;time&gt;.mp4.</param>
    public Mp4Recorder(string directory, string? outputPath, TimeSpan? segmentLength, bool overwrite, bool hasAudio)
    {
        _names = new RecordingNames(directory, outputPath, segmentLength is not null, overwrite);
        _segmentLength = segmentLength?.Ticks;
        _hasAudio = hasAudio;
    }

    /// <summary>The folder the recording files are written to.</summary>
    public string Folder => _names.Folder;

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

            string[] files = [.. _tracks.Select(track => track.Segment?.File.Path ?? _names.ForFile(track.StreamPath, Math.Max(1, track.FileCount)))];
            long duration = _tracks.Count == 0 ? 0 : _tracks.Max(track => track.Segment?.File.Duration ?? 0);
            return new RecordingStatus(_state, files, duration, _recordingBytes);
        }
    }

    /// <summary>
    /// Sets the video streams to record. While a recording is in progress, the files of the previous
    /// streams are finished and the recording carries on in new files for the new streams.
    /// </summary>
    public void SetSources(IReadOnlyList<RecordingSource> sources)
    {
        bool restarted;
        lock (_lock)
        {
            _sources = sources;
            restarted = _state != RecordingState.Stopped;
            FinishFilesLocked();
            _tracks = restarted ? CreateTracksLocked(NextRecordingPathLocked()) : [];
        }

        if (restarted)
        {
            RequestKeyFrames(sources);
            StateChanged?.Invoke();
        }
    }

    /// <summary>Starts a new recording (new files), or resumes a paused one. Throws <see cref="RefusedException"/> if the files may not be created.</summary>
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
                    _tracks = CreateTracksLocked(NextRecordingPathLocked());
                    _recordingBytes = 0;
                    _state = RecordingState.Recording;
                    break;
            }

            sources = _sources;
        }

        RequestKeyFrames(sources);
        StateChanged?.Invoke();
    }

    /// <summary>Ends the recording and finalizes its files.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_state == RecordingState.Stopped)
            {
                return;
            }

            _state = RecordingState.Stopped;
            FinishFilesLocked();
            _tracks = [];
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
                    track.PausedAt = segment.SessionTime(segment.File.Duration);
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

        RequestKeyFrames(sources);
        StateChanged?.Invoke();
    }

    public void WriteVideo(string sourceId, EncodedVideoFrame frame)
    {
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
                    if (track.Segment is { } current && track.ResumePending)
                    {
                        // Everything between the pause and this key frame is cut out of the file's timeline.
                        current.PausedTotal += frame.Timestamp - track.PausedAt;
                        current.ResumedAt = frame.Timestamp;
                        current.AudioResyncPending = true;
                        track.ResumePending = false;
                    }

                    if (track.Segment is null || ShouldStartNewFile(track.Segment, frame))
                    {
                        StartNextFileLocked(track, frame);
                    }
                }

                if (track.Segment is not { } segment || track.ResumePending)
                {
                    return; // a file, and the part after a pause, has to begin with a key frame
                }

                long time = segment.FileTime(frame.Timestamp);
                if (_segmentLength is { } length && !track.SegmentKeyFrameRequested && time + frame.Duration >= length)
                {
                    // Asked for ahead of time, so that the next file can start on time.
                    track.SegmentKeyFrameRequested = true;
                    track.Source.RequestKeyFrame();
                }

                segment.File.WriteVideo(frame.Data, time, frame.Duration, frame.IsKeyFrame);
                _recordingBytes += frame.Data.Length;
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

    public void WriteAudio(AudioFrame frame)
    {
        bool failed = false;
        lock (_lock)
        {
            if (_state == RecordingState.Stopped || !_hasAudio)
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

    /// <summary>Finalizes the open files and waits for every file still being finalized.</summary>
    public void Dispose()
    {
        Task[] pending;
        lock (_lock)
        {
            _disposed = true;
            _state = RecordingState.Stopped;
            FinishFilesLocked();
            _tracks = [];
            pending = [.. _finalizing];
            _finalizing.Clear();
        }

        Task.WaitAll(pending);
    }

    private static void RequestKeyFrames(IReadOnlyList<RecordingSource> sources)
    {
        // A file has to begin with a key frame; ask for one rather than wait for the next scheduled one.
        foreach (RecordingSource source in sources)
        {
            source.RequestKeyFrame();
        }
    }

    /// <summary>
    /// Settles the name of a new recording. A recording only counts once its name is settled, so
    /// that a refused start (the name is taken) does not move later recordings on to the next number.
    /// </summary>
    private string NextRecordingPathLocked()
    {
        string path = _names.ForRecording(_recordingCount + 1, [.. _sources.Select(source => source.Id)], DateTime.Now, File.Exists);
        _recordingCount++;
        return path;
    }

    private List<Track> CreateTracksLocked(string recordingPath) =>
        [.. _sources.Select(source => new Track(source, RecordingNames.ForStream(recordingPath, source.Id, _sources.Count)))];

    private void ResumeLocked()
    {
        _state = RecordingState.Recording;

        // With a file open, writing picks up again at the next key frame; without one, the file simply starts there.
        foreach (Track track in _tracks)
        {
            track.ResumePending = track.Segment is not null;
        }
    }

    private bool ShouldStartNewFile(Segment segment, EncodedVideoFrame keyFrame)
    {
        // A different SPS/PPS means a different encoder configuration (a new frame rate, or a fall
        // back from hardware to software); one MP4 video track cannot hold both.
        if (!keyFrame.SequenceHeader.AsSpan().SequenceEqual(segment.SequenceHeader))
        {
            return true;
        }

        return _segmentLength is { } length && segment.FileTime(keyFrame.Timestamp) >= length;
    }

    /// <summary>Finishes the track's file, if it has one, and starts its next file with this key frame.</summary>
    private void StartNextFileLocked(Track track, EncodedVideoFrame keyFrame)
    {
        if (track.Segment is { } finished)
        {
            // Detached before the next file is created: should creating it fail, FailLocked
            // finishes the track's file, and it must not finish this one a second time.
            track.Segment = null;
            FinishLaterLocked(finished.File);
        }

        string path = _names.ForFile(track.StreamPath, ++track.FileCount);
        track.SegmentKeyFrameRequested = false;
        Mp4FileWriter file = Mp4FileWriter.Create(path, track.Source.VideoConfig(), keyFrame.SequenceHeader!, _hasAudio);
        track.Segment = new Segment(file, keyFrame.Timestamp, keyFrame.SequenceHeader!);
        Log.Info($"Recording to {path}");
    }

    private void WriteAudioLocked(Track track, AudioFrame frame)
    {
        if (track.Segment is not { } segment)
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

        long time = segment.FileTime(frame.Timestamp);
        if (time < 0)
        {
            return; // captured before this file's first video frame
        }

        if (segment.AudioResyncPending)
        {
            // Bridge the difference between where the audio stopped and where it resumes with
            // silence, to keep it in step with the video.
            if (!segment.File.PadAudioTo(time))
            {
                return; // from before the pause point; already covered
            }

            segment.AudioResyncPending = false;
        }

        segment.File.WriteAudio(time, MemoryMarshal.AsBytes(frame.Samples.AsSpan()));
    }

    /// <summary>Finalizes every open file in the background and detaches it from its track.</summary>
    private void FinishFilesLocked()
    {
        foreach (Track track in _tracks)
        {
            if (track.Segment is { } segment)
            {
                track.Segment = null;
                FinishLaterLocked(segment.File);
            }
        }
    }

    private void FinishLaterLocked(Mp4FileWriter file)
    {
        _finalizing.RemoveAll(task => task.IsCompleted);
        _finalizing.Add(Task.Run(file.Finish));
    }

    /// <summary>Stops recording after a write error (for example a full disk) without stopping the capture.</summary>
    private void FailLocked(Exception exception)
    {
        Log.Error($"Recording stopped: {exception.Message}");
        _state = RecordingState.Stopped;
        FinishFilesLocked();
        _tracks = [];
    }

    /// <summary>The recording of one video stream: its current file and its pause bookkeeping.</summary>
    /// <param name="streamPath">The name of the stream's files, before numbering (see <see cref="RecordingNames.ForFile"/>).</param>
    private sealed class Track(RecordingSource source, string streamPath)
    {
        public RecordingSource Source { get; } = source;

        public string StreamPath { get; } = streamPath;

        public Segment? Segment { get; set; }

        public int FileCount { get; set; }

        public bool SegmentKeyFrameRequested { get; set; }

        /// <summary>Set by a resume: writing restarts at the next key frame.</summary>
        public bool ResumePending { get; set; }

        /// <summary>Session-clock time at which the last pause began.</summary>
        public long PausedAt { get; set; }
    }

    /// <summary>A file being written for a track, and how its timeline relates to the session clock.</summary>
    private sealed class Segment(Mp4FileWriter file, long origin, byte[] sequenceHeader)
    {
        public Mp4FileWriter File { get; } = file;

        /// <summary>The SPS/PPS the file's video was set up with.</summary>
        public byte[] SequenceHeader { get; } = sequenceHeader;

        /// <summary>Total time cut out of this file by pauses.</summary>
        public long PausedTotal { get; set; }

        /// <summary>Session-clock time at which writing last resumed after a pause.</summary>
        public long ResumedAt { get; set; }

        /// <summary>Set after a pause: the next audio frame has to be lined up with the video again.</summary>
        public bool AudioResyncPending { get; set; }

        /// <summary>Where a session-clock time falls on the file's timeline, which starts at zero at its first video frame.</summary>
        public long FileTime(long sessionTime) => sessionTime - origin - PausedTotal;

        /// <summary>The session-clock time of a point on the file's timeline.</summary>
        public long SessionTime(long fileTime) => fileTime + origin + PausedTotal;
    }
}
