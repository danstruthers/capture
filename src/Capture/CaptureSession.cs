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
/// <para>
/// Threads: requests arrive on the server's threads, and the recorder and the server report their
/// changes on whichever thread made them. None of those threads acts on a change: each only
/// signals the session's update thread, which then works out what has to run
/// (<see cref="UpdateDemand"/>) and sends viewers the state, once for a burst of changes and at
/// least once a second. So no class calls back into another while holding a lock of its own.
/// <c>_layoutLock</c> serializes changes to the set of streams: layout, frame rate and shutdown.
/// </para>
/// </summary>
internal sealed class CaptureSession : IRemoteControl, IDisposable
{
    /// <summary>How often viewers get the state even when nothing changed: the recording time and the viewer counts keep moving.</summary>
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(1);

    private readonly Options _options;
    private readonly MediaClock _clock = new();
    private readonly VideoSettings _videoSettings;
    private readonly AudioMixer? _audio;
    private readonly Mp4Recorder _recorder;
    private readonly StreamServer? _server;
    private readonly object _layoutLock = new();
    private readonly object _audioDeviceLock = new();

    // Not disposed: a late change notification may still signal it while the session shuts down.
    private readonly AutoResetEvent _changed = new(false);
    private readonly Thread _updateThread;

    private volatile VideoPipeline[] _pipelines = [];
    private volatile int _frameRate;
    private ScreenLayout _layout;
    private IReadOnlyList<DisplayInfo> _displays;
    private StatusLine? _statusLine;
    private IReadOnlyList<AudioDeviceInfo> _audioDevices = [];
    private long? _audioDevicesListedAt;
    private bool _started;
    private int _faulted;
    private volatile bool _disposed;

    /// <param name="screens">The screens to capture, as the options name them (<see cref="DisplayInfo.Select"/>).</param>
    /// <param name="audioSelection">What to record audio from (<see cref="AudioSelection"/>), when audio is on.</param>
    public CaptureSession(Options options, IReadOnlyList<DisplayInfo> screens, IReadOnlyList<string> audioSelection)
    {
        _options = options;
        _frameRate = options.Fps;
        _layout = options.Layout;
        _updateThread = new Thread(UpdateLoop) { IsBackground = true, Name = "session-updates" };
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

        _displays = screens;
        VideoPipeline[] pipelines = CreatePipelines(_layout, _displays);

        try
        {
            if (options.Audio)
            {
                var supervisor = new AudioDeviceSupervisor(_clock, AudioDevices.System, audioSelection);
                _audio = new AudioMixer(_clock, supervisor);
                Log.Info($"Audio: {DescribeAudioSelection(supervisor)} -> 16-bit PCM, {PcmFormat.SampleRate} Hz, mono");
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
                var settings = new StreamSettings(options.Bind, options.Port, options.AllowedOrigins, options.Stun, options.Profile, options.RemoteControl);
                StreamingState initial = options.Stream ? StreamingState.Streaming : StreamingState.Stopped;
                _server = new StreamServer(settings, this, initial, options.VideoMode, _audio is not null);
                _server.Changed += OnStateChanged;
            }

            Install(pipelines);

            if (_audio is not null)
            {
                _audio.FrameMixed += OnAudioFrame;
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

    public void Start()
    {
        if (_server is not null)
        {
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

        _changed.Set(); // the first update: switch on what is already needed
        _updateThread.Start();

        if (_options.StatsSeconds > 0)
        {
            TimeSpan interval = TimeSpan.FromSeconds(_options.StatsSeconds);
            _statusLine = new StatusLine(interval, _clock, () => _pipelines, _recorder, _audio, _server);
        }
    }

    public StateMessage GetState()
    {
        RecordingStatus recording = _recorder.GetStatus();
        return new StateMessage(
            RemoteControl: _options.RemoteControl,
            Layout: _layout,
            Recording: new RecordingInfo(
                recording.State,
                [.. recording.Files.Select(file => Path.GetFileName(file))],
                Math.Round(recording.Duration / (double)MediaClock.TicksPerSecond, 1),
                recording.Bytes),
            Streaming: new StreamingInfo(_server?.State ?? StreamingState.Stopped, _server?.ViewerCount ?? 0, _server?.ClientCount ?? 0),
            Video: new VideoInfo(_server?.Mode ?? _options.VideoMode, _frameRate, _options.JpegQuality),
            Sources: [.. _pipelines.Select(DescribeSource)],
            Audio: DescribeAudio());
    }

    public void Execute(CommandRequest command)
    {
        RefuseIfShuttingDown();
        switch (command.Target, command.Action)
        {
            case (CommandTarget.Recording, CommandAction.Start): _recorder.Start(); break;
            case (CommandTarget.Recording, CommandAction.Stop): _recorder.Stop(); break;
            case (CommandTarget.Recording, CommandAction.Pause): _recorder.Pause(); break;
            case (CommandTarget.Recording, CommandAction.Resume): _recorder.Resume(); break;
            case (CommandTarget.Streaming, CommandAction.Start): Server().StartStreaming(); break;
            case (CommandTarget.Streaming, CommandAction.Stop): Server().StopStreaming(); break;
            case (CommandTarget.Streaming, CommandAction.Pause): Server().PauseStreaming(); break;
            case (CommandTarget.Streaming, CommandAction.Resume): Server().ResumeStreaming(); break;
        }
    }

    /// <summary>
    /// Applies the settings of a "configure" request, which were all checked when it was read.
    /// (Applying one can still be refused by what it needs, such as an audio device that cannot be opened.)
    /// </summary>
    public void Configure(ConfigureRequest settings)
    {
        RefuseIfShuttingDown();
        if (settings.VideoMode is not null && _server is null)
        {
            throw new RefusedException("The capture client was started without its server (--no-server).");
        }

        if (settings.FrameRate is { } fps)
        {
            SetFrameRate(fps);
        }

        if (settings.VideoMode is { } mode)
        {
            Server().SetMode(mode);
        }

        if (settings.Layout is { } layout)
        {
            SetLayout(layout);
        }

        if (settings.AudioDevices is { } entries)
        {
            SetAudioSelection(entries);
        }

        if (!settings.IsEmpty)
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
            // Checked under the lock that Dispose takes to dispose the streams: streams built after that would never be disposed.
            RefuseIfShuttingDown();

            IReadOnlyList<DisplayInfo> displays;
            try
            {
                displays = DisplayInfo.Select(_options.Screens);
            }
            catch (ArgumentException ex)
            {
                throw new RefusedException(ex.Message, ex);
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
        _changed.Set();
        if (_updateThread.IsAlive)
        {
            _updateThread.Join(TimeSpan.FromSeconds(2));
        }

        _statusLine?.Dispose();

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

    /// <summary>"the default recording device (Microphone (USB Audio)) + Speakers (Realtek(R) Audio)".</summary>
    private static string DescribeAudioSelection(AudioDeviceSupervisor devices)
    {
        IReadOnlyList<AudioEntryStatus> inputs = devices.GetEntries();
        return inputs.Count == 0
            ? "nothing (silence)"
            : string.Join(" + ", inputs.Select(input => AudioSelection.KindOfDefault(input.Id) is { } kind
                ? $"the default {AudioDevices.Describe(kind)} ({input.Device?.Name ?? "there is none"})"
                : input.Device?.Name ?? input.Id));
    }

    private static string Describe(VideoMode mode) => mode == VideoMode.Jpeg ? "JPEG over WebSocket" : "H.264 over WebRTC";

    private void RefuseIfShuttingDown()
    {
        if (_disposed)
        {
            throw new RefusedException("The capture client is shutting down.");
        }
    }

    private static string Describe(ScreenLayout layout) => layout == ScreenLayout.Desktop ? "whole-desktop" : "separate-screens";

    private StreamServer Server() =>
        _server ?? throw new RefusedException("The capture client was started without its server (--no-server).");

    private VideoPipeline[] CreatePipelines(ScreenLayout layout, IReadOnlyList<DisplayInfo> displays)
    {
        IReadOnlyList<CaptureArea> areas = CaptureArea.ForLayout(layout, displays);
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

    /// <summary>The "sources" part of the state: one stream.</summary>
    private SourceInfo DescribeSource(VideoPipeline pipeline)
    {
        CaptureArea area = pipeline.Area;
        H264EncoderConfig config = pipeline.Config;
        return new SourceInfo(
            area.Id,
            area.Number,
            area.Name,
            [.. area.Displays.Select(display => display.Number)],
            area.IsPrimary,
            area.Left,
            area.Top,
            area.Width,
            area.Height,
            config.Width,
            config.Height,
            config.BitrateBps / 1000,
            pipeline.IsCapturing,
            _server?.ViewersOf(area.Id) ?? 0,
            pipeline.CaptureMethod,
            pipeline.EncoderName,
            pipeline.EncoderIsHardware);
    }

    private void SetAudioSelection(IReadOnlyList<string> entries)
    {
        AudioDeviceSupervisor devices = _audio?.Devices ?? throw new RefusedException("Audio is turned off on the capture client (--no-audio).");
        IReadOnlyList<string> before = devices.Selection;
        devices.SetSelection(entries);
        if (!devices.Selection.SequenceEqual(before))
        {
            Log.Info($"Audio is now recorded from {DescribeAudioSelection(devices)}.");
        }
    }

    /// <summary>The "audio" part of the state.</summary>
    private AudioInfo DescribeAudio()
    {
        if (_audio is null)
        {
            return AudioInfo.Off;
        }

        AudioDeviceSupervisor devices = _audio.Devices;
        IReadOnlyList<string> selection = devices.Selection;
        return new AudioInfo(
            Enabled: true,
            Source: AudioSelection.AsSource([.. selection])?.ToString().ToLowerInvariant() ?? "devices",
            Selected: selection,
            Description: devices.Description,
            SampleRate: PcmFormat.SampleRate,
            Devices: AudioDeviceList.Build(devices.GetEntries(), ConnectedAudioDevices()));
    }

    /// <summary>The connected audio devices, listed again at most every two seconds: the state goes out every second.</summary>
    private IReadOnlyList<AudioDeviceInfo> ConnectedAudioDevices()
    {
        lock (_audioDeviceLock)
        {
            long now = Environment.TickCount64;
            if (_audioDevicesListedAt is not { } listedAt || now - listedAt >= 2000)
            {
                try
                {
                    _audioDevices = AudioDevices.Enumerate();
                }
                catch (Exception ex)
                {
                    Log.Debug($"Listing the audio devices failed: {ex.Message}");
                }

                _audioDevicesListedAt = now;
            }

            return _audioDevices;
        }
    }

    private void SetFrameRate(int fps)
    {
        // Under the layout lock: a layout change builds its new streams at _frameRate, and must see
        // either the old rate (and get the change below) or the new one.
        lock (_layoutLock)
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
        }

        Log.Info($"Frame rate is now {fps} fps.");
    }

    /// <summary>Something changed (on any thread, possibly under that thread's locks): only wakes the update thread.</summary>
    private void OnStateChanged()
    {
        if (!_disposed)
        {
            _changed.Set();
        }
    }

    /// <summary>The update thread: see the class remarks.</summary>
    private void UpdateLoop()
    {
        while (!_disposed)
        {
            _changed.WaitOne(StateInterval);
            if (_disposed)
            {
                return;
            }

            try
            {
                UpdateDemand();
                BroadcastState();
            }
            catch (Exception ex)
            {
                Log.Warn($"Bringing capture in line with its use failed: {ex.Message}");
                Log.Debug(ex.ToString());
            }
        }
    }

    /// <summary>Switches capture on for exactly the streams and outputs that are in use right now. Runs on the update thread only.</summary>
    private void UpdateDemand()
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
}
