using System.Globalization;
using System.Text.Json;
using Capture.Audio;
using Capture.Output;
using Capture.Video;

namespace Capture;

/// <summary>
/// Wires the pieces together and is what remote-control requests act on.
/// <para>
/// There is one video pipeline per stream: one per screen in the separate layout, or a single one
/// for the whole desktop. Each pipeline and the audio mixer feed both the MP4 recorder and the
/// stream server, so a stream is encoded once however many outputs use it. Nothing is captured
/// unless something needs it: whenever the recording state, the streaming state, the set of viewers
/// or what they watch changes, the session works out which streams are in use and switches each
/// one's capture on or off to match.
/// </para>
/// </summary>
internal sealed class CaptureSession : IRemoteControl, IDisposable
{
    private readonly Options _options;
    private readonly MediaClock _clock = new();
    private readonly VideoSettings _videoSettings;
    private readonly AudioMixer? _audio;
    private readonly Mp4Recorder _recorder;
    private readonly StreamServer? _server;
    private readonly object _layoutLock = new();
    private readonly object _demandLock = new();
    private readonly Dictionary<string, VideoStats> _lastStats = [];

    private volatile VideoPipeline[] _pipelines = [];
    private volatile int _frameRate;
    private ScreenLayout _layout;
    private IReadOnlyList<DisplayInfo> _displays;
    private Timer? _statsTimer;
    private Timer? _stateTimer;
    private long _lastAudioFrames;
    private long _lastStatsTime;
    private bool _wasIdle;
    private bool _started;
    private int _faulted;
    private bool _disposed;

    public CaptureSession(Options options)
    {
        _options = options;
        _frameRate = options.Fps;
        _layout = options.Layout;
        _videoSettings = new VideoSettings(
            options.Fps,
            options.BitrateKbps * 1000,
            options.GopSeconds,
            options.Profile,
            options.Encoder,
            options.MaxWidth,
            options.MaxHeight,
            options.DrawCursor,
            options.Capture,
            options.JpegQuality);

        _displays = DisplayInfo.Select(options.Screens);
        VideoPipeline[] pipelines = CreatePipelines(_layout, _displays);

        try
        {
            if (options.Audio)
            {
                if (options.AudioDevice is not null)
                {
                    AudioDevices.Find(options.AudioDevice); // fail now, not when the microphone is first needed
                }

                _audio = new AudioMixer(_clock, options.AudioDevice, options.AudioSource);
                Log.Info($"Audio: {AudioMixer.Describe(options.AudioSource)} -> 16-bit PCM, {PcmFormat.SampleRate} Hz, mono");
            }
            else
            {
                Log.Info("Audio: off");
            }

            TimeSpan? segment = options.SegmentSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
            _recorder = new Mp4Recorder(options.OutputDirectory, options.Output, segment, options.Overwrite, _audio is not null);
            _recorder.StateChanged += OnStateChanged;
            Log.Info($"Recordings are saved in {_recorder.Folder}");

            if (options.Server)
            {
                var settings = new StreamSettings(options.Bind, options.Port, options.AllowedOrigins, options.Stun, options.Profile);
                StreamingState initial = options.Stream ? StreamingState.Streaming : StreamingState.Stopped;
                _server = new StreamServer(settings, this, initial, options.VideoMode, _audio is not null);
                _server.Changed += OnStateChanged;
            }

            Install(pipelines);

            if (_audio is not null)
            {
                _audio.FrameCaptured += OnAudioFrame;
            }
        }
        catch
        {
            foreach (VideoPipeline pipeline in pipelines)
            {
                pipeline.Dispose();
            }

            _recorder?.Dispose();
            _audio?.Dispose();
            throw;
        }
    }

    /// <summary>Raised once when capture cannot continue.</summary>
    public event Action<Exception>? Faulted;

    public Exception? Failure { get; private set; }

    bool IRemoteControl.Enabled => _options.RemoteControl;

    public void Start()
    {
        if (_server is not null)
        {
            SipSorceryLogging.Install();
            _server.Start();
            string host = _options.Bind.Equals(System.Net.IPAddress.Any) ? "<this machine>" : _options.Bind.ToString();
            string control = _options.RemoteControl ? "watch and control" : "watch";
            Log.Info($"Listening on ws://{host}:{_options.Port}/ - open the viewer page to {control}. " +
                     $"Streaming is {(_options.Stream ? $"on ({Describe(_options.VideoMode)})" : "off until a viewer starts it")}.");
        }

        _audio?.Start();
        lock (_layoutLock)
        {
            foreach (VideoPipeline pipeline in _pipelines)
            {
                pipeline.Start();
            }

            _started = true;
        }

        if (_options.Record)
        {
            _recorder.Start();
        }
        else
        {
            Log.Info("Recording is off until a viewer starts it.");
        }

        UpdateDemand();

        if (_options.StatsSeconds > 0)
        {
            TimeSpan interval = TimeSpan.FromSeconds(_options.StatsSeconds);
            _lastStatsTime = _clock.Now;
            _statsTimer = new Timer(_ => PrintStats(), null, interval, interval);
        }

        if (_server is not null)
        {
            // Keeps the figures viewers see (recording time and size, viewer counts) moving.
            _stateTimer = new Timer(_ => BroadcastState(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public string GetStateJson()
    {
        VideoPipeline[] pipelines = _pipelines;
        RecordingStatus recording = _recorder.GetStatus();

        return JsonSerializer.Serialize(new
        {
            type = "state",
            remoteControl = _options.RemoteControl,
            layout = _layout.ToString().ToLowerInvariant(),
            recording = new
            {
                state = recording.State.ToString().ToLowerInvariant(),
                files = recording.Files.Select(Path.GetFileName).ToArray(),
                seconds = Math.Round(recording.Duration / (double)MediaClock.TicksPerSecond, 1),
                bytes = recording.Bytes,
            },
            streaming = new
            {
                state = (_server?.State ?? StreamingState.Stopped).ToString().ToLowerInvariant(),
                viewers = _server?.ViewerCount ?? 0,
                clients = _server?.ClientCount ?? 0,
            },
            video = new
            {
                mode = (_server?.Mode ?? _options.VideoMode).ToString().ToLowerInvariant(),
                frameRate = _frameRate,
                jpegQuality = _options.JpegQuality,
            },
            sources = pipelines.Select(pipeline =>
            {
                CaptureArea area = pipeline.Area;
                H264EncoderConfig config = pipeline.Config;
                return new
                {
                    id = area.Id,
                    number = area.Number,
                    name = area.Name,
                    screens = area.Displays.Select(display => display.Number).ToArray(),
                    primary = area.IsPrimary,
                    left = area.Left,
                    top = area.Top,
                    width = area.Width,
                    height = area.Height,
                    videoWidth = config.Width,
                    videoHeight = config.Height,
                    bitrateKbps = config.BitrateBps / 1000,
                    capturing = pipeline.IsCapturing,
                    viewers = _server?.ViewersOf(area.Id) ?? 0,
                    capture = pipeline.CaptureMethod,
                    encoder = pipeline.EncoderName,
                    hardware = pipeline.EncoderIsHardware,
                };
            }).ToArray(),
            audio = new
            {
                enabled = _audio is not null,
                source = (_audio?.Source ?? AudioSourceKind.None).ToString().ToLowerInvariant(),
                description = _audio?.Description,
                sampleRate = PcmFormat.SampleRate,
            },
        });
    }

    public void Execute(string target, string action)
    {
        switch (target, action)
        {
            case ("recording", "start"): _recorder.Start(); break;
            case ("recording", "stop"): _recorder.Stop(); break;
            case ("recording", "pause"): _recorder.Pause(); break;
            case ("recording", "resume"): _recorder.Resume(); break;
            case ("streaming", "start"): Server().StartStreaming(); break;
            case ("streaming", "stop"): Server().StopStreaming(); break;
            case ("streaming", "pause"): Server().PauseStreaming(); break;
            case ("streaming", "resume"): Server().ResumeStreaming(); break;
            default: throw new InvalidOperationException($"'{action} {target}' is not a command this capture client knows.");
        }
    }

    public void Configure(JsonElement settings)
    {
        bool changed = false;

        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            switch (setting.Name)
            {
                case "type":
                    continue;

                case "frameRate":
                    if (!setting.Value.TryGetInt32(out int fps) || fps is < 1 or > 60)
                    {
                        throw new InvalidOperationException("The frame rate has to be a whole number from 1 to 60.");
                    }

                    SetFrameRate(fps);
                    break;

                case "videoMode":
                    if (!Enum.TryParse(setting.Value.GetString(), ignoreCase: true, out VideoMode mode) || !Enum.IsDefined(mode))
                    {
                        throw new InvalidOperationException("The video mode has to be h264 or jpeg.");
                    }

                    Server().SetMode(mode);
                    break;

                case "layout":
                    if (!Enum.TryParse(setting.Value.GetString(), ignoreCase: true, out ScreenLayout layout) || !Enum.IsDefined(layout))
                    {
                        throw new InvalidOperationException("The layout has to be separate or desktop.");
                    }

                    SetLayout(layout);
                    break;

                case "audioSource":
                    if (!Enum.TryParse(setting.Value.GetString(), ignoreCase: true, out AudioSourceKind source) || !Enum.IsDefined(source))
                    {
                        throw new InvalidOperationException("The audio source has to be microphone, desktop, both or none.");
                    }

                    if (_audio is null)
                    {
                        throw new InvalidOperationException("Audio is turned off on the capture client (--no-audio).");
                    }

                    _audio.SetSource(source);
                    Log.Info($"Audio source is now {AudioMixer.Describe(source)}.");
                    break;

                default:
                    throw new InvalidOperationException($"'{setting.Name}' is not a setting this capture client knows.");
            }

            changed = true;
        }

        if (changed)
        {
            OnStateChanged();
        }
    }

    /// <summary>
    /// Switches between one stream per screen and one stream for the whole desktop. The screens are
    /// looked up again, so this also picks up screens that were added, removed or rearranged. A
    /// recording in progress continues in new files.
    /// </summary>
    public void SetLayout(ScreenLayout layout)
    {
        lock (_layoutLock)
        {
            IReadOnlyList<DisplayInfo> displays;
            try
            {
                displays = DisplayInfo.Select(_options.Screens);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }

            if (layout == _layout && displays.SequenceEqual(_displays))
            {
                return;
            }

            Log.Info(layout == _layout ? "The screens have changed; setting the streams up again." : $"Switching to the {Describe(layout)} layout.");

            // Build the new streams before taking down the old ones, so a failure leaves things as they were.
            // A screen can only be duplicated once at a time, so the new streams take each screen's
            // desktop duplication over from the old ones when they start capturing.
            VideoPipeline[] created = CreatePipelines(layout, displays);

            // The old streams' last frames still reach the recorder and the viewers while they shut down.
            foreach (VideoPipeline pipeline in _pipelines)
            {
                pipeline.Dispose();
            }

            _layout = layout;
            _displays = displays;
            Install(created);

            if (_started)
            {
                foreach (VideoPipeline pipeline in created)
                {
                    pipeline.Start();
                }
            }
        }

        OnStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statsTimer?.Dispose();
        _stateTimer?.Dispose();

        // Sources first, so the last frames reach the outputs before those are closed.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<string>();
        void Step(string name, Action action)
        {
            long started = timer.ElapsedMilliseconds;
            action();
            steps.Add($"{name} {timer.ElapsedMilliseconds - started} ms");
        }

        lock (_layoutLock)
        {
            foreach (VideoPipeline pipeline in _pipelines)
            {
                Step(pipeline.Area.Id, pipeline.Dispose);
            }
        }

        Step("audio", () => _audio?.Dispose());
        Step("server", () => _server?.Dispose());
        Step("recorder", _recorder.Dispose);
        Log.Debug($"Shut down: {string.Join(", ", steps)}.");
    }

    private static string Describe(VideoMode mode) => mode == VideoMode.Jpeg ? "JPEG over WebSocket" : "H.264 over WebRTC";

    private static string Describe(ScreenLayout layout) => layout == ScreenLayout.Desktop ? "whole-desktop" : "separate-screens";

    private static string Format(double milliseconds) => milliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    private StreamServer Server() =>
        _server ?? throw new InvalidOperationException("The capture client was started without its server (--no-server).");

    private VideoPipeline[] CreatePipelines(ScreenLayout layout, IReadOnlyList<DisplayInfo> displays)
    {
        IReadOnlyList<CaptureArea> areas = CaptureArea.For(layout, displays);
        var created = new List<VideoPipeline>();
        try
        {
            foreach (CaptureArea area in areas)
            {
                created.Add(new VideoPipeline(area, _videoSettings with { Fps = _frameRate }, _clock));
            }
        }
        catch
        {
            foreach (VideoPipeline pipeline in created)
            {
                pipeline.Dispose();
            }

            throw;
        }

        string screens = string.Join(", ", displays.Select(display => display.Number));
        string plural = displays.Count > 1 ? "s" : string.Empty;
        Log.Info(layout == ScreenLayout.Desktop
            ? $"Video: one stream of the whole desktop (screen{plural} {screens})"
            : $"Video: one stream per screen (screen{plural} {screens})");

        foreach (VideoPipeline pipeline in created)
        {
            CaptureArea area = pipeline.Area;
            H264EncoderConfig config = pipeline.Config;
            Log.Info($"  {area.Name}: {area.Width}x{area.Height} at ({area.Left},{area.Top}) -> {config.Width}x{config.Height} @ {config.Fps} fps, " +
                     $"H.264 {config.Profile} {config.BitrateBps / 1000} kbit/s; {pipeline.CaptureMethod}; " +
                     $"{pipeline.EncoderName} ({(pipeline.EncoderIsHardware ? "hardware" : "software")})");
        }

        return [.. created];
    }

    /// <summary>Makes <paramref name="pipelines"/> the current streams and tells the outputs about them.</summary>
    private void Install(VideoPipeline[] pipelines)
    {
        foreach (VideoPipeline pipeline in pipelines)
        {
            string id = pipeline.Area.Id;
            pipeline.FrameEncoded += frame =>
            {
                _recorder.WriteVideo(id, frame);
                _server?.SendVideo(id, frame);
            };
            pipeline.JpegEncoded += frame => _server?.SendPicture(id, frame);
            pipeline.Faulted += OnVideoFaulted;
        }

        _pipelines = pipelines;
        _recorder.SetSources([.. pipelines.Select(p => new RecordingSource(p.Area.Id, () => p.Config, p.RequestKeyFrame))]);
        _server?.SetSources([.. pipelines.Select(p => new StreamSource(p.Area.Id, p.Area.Number, p.RequestKeyFrame))]);
    }

    private void SetFrameRate(int fps)
    {
        if (fps == _frameRate)
        {
            return;
        }

        _frameRate = fps;
        foreach (VideoPipeline pipeline in _pipelines)
        {
            pipeline.SetFrameRate(fps);
        }

        Log.Info($"Frame rate is now {fps} fps.");
    }

    private void OnStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        UpdateDemand();
        BroadcastState();
    }

    /// <summary>Switches capture on for exactly the streams and outputs that are in use right now.</summary>
    private void UpdateDemand()
    {
        lock (_demandLock)
        {
            bool recording = _recorder.WantsMedia;
            foreach (VideoPipeline pipeline in _pipelines)
            {
                string id = pipeline.Area.Id;
                pipeline.SetDemand(
                    h264: recording || (_server?.WantsH264(id) ?? false),
                    jpeg: _server?.WantsJpeg(id) ?? false);
            }

            _audio?.SetActive(recording || (_server?.WantsAudio ?? false));
        }
    }

    private void BroadcastState()
    {
        try
        {
            _server?.BroadcastState();
        }
        catch (Exception ex)
        {
            Log.Debug($"State broadcast failed: {ex.Message}");
        }
    }

    private void OnAudioFrame(AudioFrame frame)
    {
        _recorder.WriteAudio(frame);
        _server?.SendAudio(frame);
    }

    private void OnVideoFaulted(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
        {
            Log.Error($"Video capture failed: {exception.Message}");
            Log.Debug(exception.ToString());
            Failure = exception;
            Faulted?.Invoke(exception);
        }
    }

    private void PrintStats()
    {
        try
        {
            long now = _clock.Now;
            double seconds = (now - _lastStatsTime) / (double)MediaClock.TicksPerSecond;
            if (seconds <= 0)
            {
                return;
            }

            VideoPipeline[] pipelines = _pipelines;
            RecordingStatus recording = _recorder.GetStatus();
            var parts = new List<string>();
            var current = new Dictionary<string, VideoStats>();

            foreach (VideoPipeline pipeline in pipelines)
            {
                string id = pipeline.Area.Id;
                VideoStats stats = pipeline.GetStats();
                VideoStats last = _lastStats.GetValueOrDefault(id);
                current[id] = stats;

                long captured = stats.FramesCaptured - last.FramesCaptured;
                if (captured == 0)
                {
                    continue;
                }

                long encoded = stats.FramesEncoded - last.FramesEncoded;
                long pictures = stats.JpegFrames - last.JpegFrames;
                long dropped = stats.FramesDropped - last.FramesDropped;

                string line = $"{id} {captured / seconds:0.0} fps, capture {Format((stats.CaptureMs - last.CaptureMs) / captured)} ms";
                if (encoded > 0)
                {
                    line += $", H.264 {(stats.BytesEncoded - last.BytesEncoded) * 8 / seconds / 1000:0} kbit/s " +
                            $"(convert {Format((stats.ConvertMs - last.ConvertMs) / encoded)} ms, encode {Format((stats.EncodeMs - last.EncodeMs) / encoded)} ms)";
                }

                if (pictures > 0)
                {
                    line += $", JPEG {pictures / seconds:0.0} fps {(stats.JpegBytes - last.JpegBytes) * 8 / seconds / 1000:0} kbit/s " +
                            $"({Format((stats.JpegMs - last.JpegMs) / pictures)} ms)";
                }

                if (dropped > 0)
                {
                    line += $", dropped {dropped}";
                }

                parts.Add(line);
            }

            _lastStats.Clear();
            foreach ((string id, VideoStats stats) in current)
            {
                _lastStats[id] = stats;
            }

            bool idle = parts.Count == 0 && recording.State != RecordingState.Recording;
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
                    long audioFrames = _audio.FramesProduced;
                    parts.Add($"audio {(audioFrames - _lastAudioFrames) * PcmFormat.SamplesPerFrame / seconds:0} samples/s");
                    _lastAudioFrames = audioFrames;
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
            _lastStatsTime = now;
        }
        catch (ObjectDisposedException)
        {
            // The timer fired while the session was shutting down.
        }
    }
}
