using System.Globalization;
using Capture.Audio;
using Capture.Output;
using Capture.Video;

namespace Capture;

/// <summary>
/// The line the capture client logs every few seconds (--stats): for each stream that is being
/// captured, its frame rate and where the time per frame goes; then audio, streaming and
/// recording. When nothing is being captured it says so once instead.
/// </summary>
internal sealed class StatusLine : IDisposable
{
    private readonly MediaClock _clock;
    private readonly Func<IReadOnlyList<VideoPipeline>> _pipelines;
    private readonly Mp4Recorder _recorder;
    private readonly AudioMixer? _audio;
    private readonly StreamServer? _server;
    private readonly Timer _timer;

    // The figures at the last line, to report the change since. Only the timer thread uses them.
    private readonly Dictionary<string, VideoStats> _lastStats = [];
    private long _lastAudioFrames;
    private long _lastTime;
    private bool _wasIdle;

    /// <param name="pipelines">The current streams; they change with the layout.</param>
    public StatusLine(TimeSpan interval, MediaClock clock, Func<IReadOnlyList<VideoPipeline>> pipelines, Mp4Recorder recorder, AudioMixer? audio, StreamServer? server)
    {
        _clock = clock;
        _pipelines = pipelines;
        _recorder = recorder;
        _audio = audio;
        _server = server;
        _lastTime = clock.Now;
        _timer = new Timer(_ => Print(), null, interval, interval);
    }

    public void Dispose() => _timer.Dispose();

    private static string Milliseconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private void Print()
    {
        try
        {
            long now = _clock.Now;
            double seconds = (now - _lastTime) / (double)MediaClock.TicksPerSecond;
            if (seconds <= 0)
            {
                return;
            }

            var parts = new List<string>();
            var current = new Dictionary<string, VideoStats>();
            foreach (VideoPipeline pipeline in _pipelines())
            {
                string id = pipeline.Id;
                VideoStats stats = pipeline.GetStats();
                current[id] = stats;
                if (DescribeStream(id, stats, _lastStats.GetValueOrDefault(id), seconds) is { } line)
                {
                    parts.Add(line);
                }
            }

            _lastStats.Clear();
            foreach ((string id, VideoStats stats) in current)
            {
                _lastStats[id] = stats;
            }

            // Read every time, so that the first line after an idle spell covers only its own interval.
            long audioFrames = _audio?.FramesProduced ?? 0;
            long newAudioFrames = audioFrames - _lastAudioFrames;
            _lastAudioFrames = audioFrames;

            RecordingStatus recording = _recorder.GetStatus();

            // Someone listening to the sound alone keeps the audio devices open: not idle.
            bool idle = parts.Count == 0 && newAudioFrames == 0 && recording.State != RecordingState.Recording;
            if (idle)
            {
                if (!_wasIdle)
                {
                    Log.Info("Idle: nothing is being recorded or watched, so nothing is being captured.");
                }
            }
            else
            {
                if (_audio is not null)
                {
                    parts.Add($"audio {newAudioFrames * PcmFormat.SamplesPerFrame / seconds:0} samples/s");
                }

                if (_server is not null)
                {
                    parts.Add(_server.State switch
                    {
                        StreamingState.Stopped => "streaming stopped",
                        StreamingState.Paused => "streaming paused",
                        _ => $"streaming to {_server.ViewerCount} of {_server.ClientCount} clients",
                    });
                }

                parts.Add(recording.State switch
                {
                    RecordingState.Stopped => "not recording",
                    RecordingState.Paused => $"recording paused at {TimeSpan.FromTicks(recording.Duration):hh\\:mm\\:ss}",
                    _ => $"recorded {TimeSpan.FromTicks(recording.Duration):hh\\:mm\\:ss}, {recording.Bytes / 1_048_576.0:0.0} MB",
                });

                Log.Info(string.Join(" | ", parts));
            }

            _wasIdle = idle;
            _lastTime = now;
        }
        catch (ObjectDisposedException)
        {
            // The timer fired while the capture client was shutting down.
        }
    }

    /// <summary>"screen1 29.8 fps, capture 7.1 ms, H.264 2400 kbit/s (convert 3.0 ms, encode 9.2 ms)", or null if it captured nothing.</summary>
    private static string? DescribeStream(string id, VideoStats stats, VideoStats last, double seconds)
    {
        long captured = stats.FramesCaptured - last.FramesCaptured;
        if (captured == 0)
        {
            return null;
        }

        long encoded = stats.FramesEncoded - last.FramesEncoded;
        long pictures = stats.JpegFrames - last.JpegFrames;
        long dropped = stats.FramesDropped - last.FramesDropped;

        string line = $"{id} {captured / seconds:0.0} fps, capture {Milliseconds((stats.CaptureMs - last.CaptureMs) / captured)} ms";
        if (encoded > 0)
        {
            line += $", H.264 {(stats.BytesEncoded - last.BytesEncoded) * 8 / seconds / 1000:0} kbit/s " +
                    $"(convert {Milliseconds((stats.ConvertMs - last.ConvertMs) / encoded)} ms, encode {Milliseconds((stats.EncodeMs - last.EncodeMs) / encoded)} ms)";
        }

        if (pictures > 0)
        {
            line += $", JPEG {pictures / seconds:0.0} fps {(stats.JpegBytes - last.JpegBytes) * 8 / seconds / 1000:0} kbit/s " +
                    $"({Milliseconds((stats.JpegMs - last.JpegMs) / pictures)} ms)";
        }

        if (dropped > 0)
        {
            line += $", dropped {dropped}";
        }

        return line;
    }
}
