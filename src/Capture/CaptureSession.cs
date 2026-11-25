using Capture.Audio;
using Capture.Output;
using Capture.Video;

namespace Capture;

/// <summary>
/// Wires the pieces together and is what remote-control requests act on.
/// <para>
/// There is one video pipeline per stream: one per screen in the separate layout, or a single one
/// for the whole desktop, and one more for the camera when one is chosen. Each pipeline and the
/// audio mixer feed both the MP4 recorder and the stream server, so a stream is encoded once
/// however many outputs use it. Nothing is captured unless something needs it: whenever the
/// recording state, the streaming state, the set of viewers or what they watch changes, the
/// session works out which streams are in use and switches each one's capture on or off to match.
/// </para>
/// <para>
/// Threads: requests arrive on the server's threads, and the recorder and the server report their
/// changes on whichever thread made them. None of those threads acts on a change: each only
/// signals the session's update thread, which then works out what has to run
/// (<see cref="UpdateDemand"/>) and sends viewers the state, once for a burst of changes and at
/// least once a second. So no class calls back into another while holding a lock of its own.
/// <c>_layoutLock</c> serializes changes to the set of streams: layout, frame rates, the camera
/// and shutdown.
/// </para>
/// </summary>
internal sealed class CaptureSession : IRemoteControl, IDisposable
{
    /// <summary>How often viewers get the state even when nothing changed: the recording time and the viewer counts keep moving.</summary>
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a list of connected devices is used before it is made again: the state goes out every second.</summary>
    private const long DeviceListMilliseconds = 2000;

    private readonly Options _options;
    private readonly MediaClock _clock = new();
    private readonly VideoSettings _videoSettings;
    private readonly AudioMixer? _audio;
    private readonly Mp4Recorder _recorder;
    private readonly StreamServer? _server;
    private readonly object _layoutLock = new();
    private readonly object _deviceListLock = new();

    // Not disposed: a late change notification may still signal it while the session shuts down.
    private readonly AutoResetEvent _changed = new(false);
    private readonly Thread _updateThread;

    /// <summary>Every stream: the screens', then the camera's. Read without a lock; replaced as a whole under <c>_layoutLock</c>.</summary>
    private volatile VideoPipeline[] _pipelines = [];
    private VideoPipeline[] _screens = [];
    private VideoPipeline? _camera;
    private CameraFailure? _failedCamera;
    private volatile int _frameRate;
    private volatile int _cameraFrameRate;
    private volatile string? _cameraProblem;
    private ScreenLayout _layout;
    private IReadOnlyList<DisplayInfo> _displays;
    private StatusLine? _statusLine;
    private IReadOnlyList<AudioDeviceInfo> _audioDevices = [];
    private long? _audioDevicesListedAt;
    private IReadOnlyList<CameraDevice> _cameras = [];
    private long? _camerasListedAt;
    private bool _started;
    private int _faulted;
    private volatile bool _disposed;

    /// <param name="screens">The screens to capture, as the options name them (<see cref="DisplayInfo.Select"/>).</param>
    /// <param name="audioSelection">What to record audio from (<see cref="AudioSelection"/>), when audio is on.</param>
    /// <param name="camera">The camera to capture as well, if any. If it cannot be used, the capture starts without it and viewers are told why.</param>
    public CaptureSession(Options options, IReadOnlyList<DisplayInfo> screens, IReadOnlyList<string> audioSelection, CameraDevice? camera = null)
    {
        _options = options;
        _frameRate = options.Fps;
        _cameraFrameRate = options.CameraFps;
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
        VideoPipeline[] pipelines = CreateScreenPipelines(_layout, _displays);

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

            foreach (VideoPipeline pipeline in pipelines)
            {
                Wire(pipeline);
            }

            _screens = pipelines;
            if (camera is not null)
            {
                try
                {
                    _camera = CreateCameraPipeline(camera, _cameraFrameRate);
                }
                catch (Exception ex)
                {
                    _cameraProblem = $"{camera.Name} cannot be used: {ex.Message}";
                    Log.Warn($"{_cameraProblem} Starting without the camera.");
                }
            }

            Publish();

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

            _camera?.Dispose();
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
            Audio: DescribeAudio(),
            Camera: DescribeCamera());
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
    /// (Applying one can still be refused by what it needs, such as an audio device or a camera that cannot be opened.)
    /// </summary>
    public void Configure(ConfigureRequest settings)
    {
        RefuseIfShuttingDown();
        if (settings.VideoMode is not null && _server is null)
        {
            throw new RefusedException("The capture client was started without its server (--no-server).");
        }

        try
        {
            if (settings.FrameRate is { } fps)
            {
                SetFrameRate(fps);
            }

            // Together, so that a camera switched on with a rate opens at that rate, and the camera
            // being switched from is not set up again for it first.
            if (settings.Camera is { } camera)
            {
                SetCamera(camera == ConfigureRequest.NoCamera ? null : camera, settings.CameraFrameRate);
            }
            else if (settings.CameraFrameRate is { } cameraFps)
            {
                SetCameraFrameRate(cameraFps);
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
        }
        finally
        {
            // Whatever did change, before a setting was refused, viewers hear about.
            if (!settings.IsEmpty)
            {
                OnStateChanged();
            }
        }
    }

    /// <summary>
    /// Switches between one stream per screen and one stream for the whole desktop. The screens are
    /// looked up again, so this also picks up screens that were added, removed or rearranged. A
    /// recording in progress continues in new files. The camera's stream is not touched.
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
            VideoPipeline[] created = CreateScreenPipelines(layout, displays);

            // The old streams' last frames still reach the recorder and the viewers while they shut down.
            foreach (VideoPipeline pipeline in _screens)
            {
                pipeline.Dispose();
            }

            _layout = layout;
            _displays = displays;
            foreach (VideoPipeline pipeline in created)
            {
                Wire(pipeline);
                if (_started)
                {
                    pipeline.Start();
                }
            }

            _screens = created;
            Publish();
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
                Step(pipeline.Id, pipeline.Dispose);
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

    private static string Describe(ScreenLayout layout) => layout == ScreenLayout.Desktop ? "whole-desktop" : "separate-screens";

    private static void LogPipeline(VideoPipeline pipeline)
    {
        IFrameSource source = pipeline.Source;
        H264EncoderConfig config = pipeline.Config;
        string where = source is ScreenCapturer screen ? $" at ({screen.Area.Left},{screen.Area.Top})" : string.Empty;
        Log.Info($"  {pipeline.Name}: {source.Width}x{source.Height}{where} -> {config.Width}x{config.Height} @ {config.Fps} fps, " +
                 $"H.264 {config.Profile} {config.BitrateBps / 1000} kbit/s; {pipeline.CaptureMethod}; " +
                 $"{pipeline.EncoderName} ({(pipeline.EncoderIsHardware ? "hardware" : "software")})");
    }

    private void RefuseIfShuttingDown()
    {
        if (_disposed)
        {
            throw new RefusedException("The capture client is shutting down.");
        }
    }

    private StreamServer Server() =>
        _server ?? throw new RefusedException("The capture client was started without its server (--no-server).");

    private VideoPipeline[] CreateScreenPipelines(ScreenLayout layout, IReadOnlyList<DisplayInfo> displays)
    {
        IReadOnlyList<CaptureArea> areas = CaptureArea.ForLayout(layout, displays);
        var created = new List<VideoPipeline>();
        try
        {
            foreach (CaptureArea area in areas)
            {
                var capturer = new ScreenCapturer(area, _videoSettings.DrawCursor, _videoSettings.CaptureMethod);
                created.Add(new VideoPipeline(capturer, _videoSettings with { Fps = _frameRate }, _clock));
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
            LogPipeline(pipeline);
        }

        return [.. created];
    }

    /// <summary>
    /// A pipeline for the camera, wired to the outputs (but not yet published or started). With
    /// <paramref name="modes"/> (the camera's modes, known from its current pipeline) the camera is
    /// not opened here, only once its pictures are wanted.
    /// </summary>
    private VideoPipeline CreateCameraPipeline(CameraDevice device, int fps, IReadOnlyList<CameraMode>? modes = null)
    {
        var capturer = new CameraCapturer(device, fps, VideoPipeline.MaxOutputSize(_videoSettings), modes);
        var pipeline = new VideoPipeline(capturer, _videoSettings with { Fps = fps }, _clock);
        Log.Info("Video: the camera");
        LogPipeline(pipeline);
        if (capturer.Mode.Fps + 0.1 < fps)
        {
            Log.Info($"  {device.Name} delivers at most {capturer.Mode.Fps:0.##} fps; its pictures are repeated to make {fps} fps.");
        }

        Wire(pipeline);
        return pipeline;
    }

    /// <summary>
    /// Sends a new pipeline's frames to the outputs. A screen that fails ends the capture; the
    /// camera is extra, so when it fails the capture carries on without it.
    /// </summary>
    private void Wire(VideoPipeline pipeline)
    {
        string id = pipeline.Id;
        pipeline.FrameEncoded += frame =>
        {
            _recorder.WriteVideo(id, frame);
            _server?.SendVideo(id, frame);
        };
        pipeline.JpegEncoded += frame => _server?.SendPicture(id, frame);
        if (pipeline.Source is CameraCapturer)
        {
            pipeline.Faulted += exception => OnCameraFaulted(pipeline, exception);
        }
        else
        {
            pipeline.Faulted += OnVideoFaulted;
        }
    }

    /// <summary>
    /// Makes the screens and the camera the current streams and tells the outputs about them: a
    /// recording continues in new files, and viewers' WebRTC sessions are set up again.
    /// </summary>
    private void Publish()
    {
        VideoPipeline[] pipelines = _camera is null ? _screens : [.. _screens, _camera];
        _pipelines = pipelines;
        _recorder.SetSources([.. pipelines.Select(p => new RecordingSource(p.Id, () => p.Config, p.RequestKeyFrame))]);
        _server?.SetSources([.. pipelines.Select(p => new StreamSource(p.Id, NumberOf(p), p.RequestKeyFrame))]);
    }

    /// <summary>Identifies a stream in binary messages.</summary>
    private static int NumberOf(VideoPipeline pipeline) =>
        pipeline.Source is ScreenCapturer screen ? screen.Area.Number : CameraDevices.StreamNumber;

    /// <summary>The "sources" part of the state: one stream.</summary>
    private SourceInfo DescribeSource(VideoPipeline pipeline)
    {
        H264EncoderConfig config = pipeline.Config;
        CaptureArea? area = (pipeline.Source as ScreenCapturer)?.Area;
        SourceKind kind = area is null ? SourceKind.Camera : area.Id == CaptureArea.DesktopId ? SourceKind.Desktop : SourceKind.Screen;
        return new SourceInfo(
            pipeline.Id,
            NumberOf(pipeline),
            kind,
            pipeline.Name,
            area is null ? [] : [.. area.Displays.Select(display => display.Number)],
            area?.IsPrimary ?? false,
            area?.Left ?? 0,
            area?.Top ?? 0,
            pipeline.Source.Width,
            pipeline.Source.Height,
            config.Width,
            config.Height,
            pipeline.FrameRate,
            config.BitrateBps / 1000,
            pipeline.IsCapturing,
            _server?.ViewersOf(pipeline.Id) ?? 0,
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

    /// <summary>The "camera" part of the state.</summary>
    private CameraState DescribeCamera()
    {
        CameraCapturer? capturer = _pipelines.Select(p => p.Source).OfType<CameraCapturer>().FirstOrDefault();
        var devices = ConnectedCameras().Select(camera => new CameraEntry(camera.Id, camera.Name, camera.Id == capturer?.Device.Id)).ToList();
        if (capturer is not null && !devices.Any(entry => entry.Selected))
        {
            devices.Add(new CameraEntry(capturer.Device.Id, capturer.Device.Name, true)); // unplugged, but still the one chosen
        }

        return new CameraState(
            capturer?.Device.Id,
            _cameraFrameRate,
            capturer?.Mode.ToString(),
            capturer?.Problem ?? (capturer is null ? _cameraProblem : null),
            devices);
    }

    /// <summary>The connected audio devices, listed again at most every two seconds.</summary>
    private IReadOnlyList<AudioDeviceInfo> ConnectedAudioDevices() =>
        Listed(ref _audioDevices, ref _audioDevicesListedAt, AudioDevices.Enumerate, "audio devices");

    /// <summary>The connected cameras, listed again at most every two seconds.</summary>
    private IReadOnlyList<CameraDevice> ConnectedCameras() =>
        Listed(ref _cameras, ref _camerasListedAt, CameraDevices.Enumerate, "cameras");

    private IReadOnlyList<T> Listed<T>(ref IReadOnlyList<T> list, ref long? listedAt, Func<IReadOnlyList<T>> enumerate, string what)
    {
        lock (_deviceListLock)
        {
            long now = Environment.TickCount64;
            if (listedAt is not { } at || now - at >= DeviceListMilliseconds)
            {
                try
                {
                    list = enumerate();
                }
                catch (Exception ex)
                {
                    Log.Debug($"Listing the {what} failed: {ex.Message}");
                }

                listedAt = now;
            }

            return list;
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
            foreach (VideoPipeline pipeline in _screens)
            {
                pipeline.SetFrameRate(fps);
            }
        }

        Log.Info($"Frame rate is now {fps} fps.");
    }

    /// <summary>Changes the camera's frame rate (see <see cref="ApplyCameraFrameRate"/>).</summary>
    private void SetCameraFrameRate(int fps)
    {
        lock (_layoutLock)
        {
            RefuseIfShuttingDown();
            ApplyCameraFrameRate(fps);
        }
    }

    /// <summary>
    /// Changes the camera's frame rate. If the camera has a better mode for the new rate (a faster
    /// one, or a larger picture at a slower rate), its stream is set up again in that mode;
    /// otherwise only the encoder follows, as it does for the screens. If that is refused, the
    /// camera and its rate stay as they were. Under <c>_layoutLock</c>.
    /// </summary>
    private void ApplyCameraFrameRate(int fps)
    {
        if (fps == _cameraFrameRate)
        {
            return;
        }

        if (_camera?.Source is CameraCapturer capturer && capturer.ModeFor(fps) != capturer.Mode)
        {
            ReplaceCamera(capturer.Device, fps, capturer.Modes);
        }
        else
        {
            _camera?.SetFrameRate(fps);
        }

        _cameraFrameRate = fps;
        Log.Info($"The camera's frame rate is now {fps} fps.");
    }

    /// <summary>Captures the camera with this id as well, or (null) no camera; at <paramref name="fps"/>, when given.</summary>
    private void SetCamera(string? id, int? fps)
    {
        lock (_layoutLock)
        {
            RefuseIfShuttingDown();
            if (id is null)
            {
                _cameraProblem = null;
                if (_camera is not null)
                {
                    Log.Info($"No longer capturing {_camera.Name}.");
                    _camera.Dispose();
                    _camera = null;
                    Publish();
                }
            }
            else if ((_camera?.Source as CameraCapturer)?.Device.Id != id)
            {
                CameraDevice device = CameraDevices.Enumerate().FirstOrDefault(camera => camera.Id == id)
                    ?? throw new RefusedException("That camera is not connected (any more).");
                int opened = fps ?? _cameraFrameRate;
                ReplaceCamera(device, opened);
                if (opened != _cameraFrameRate)
                {
                    _cameraFrameRate = opened;
                    Log.Info($"The camera's frame rate is now {opened} fps.");
                }

                return;
            }

            // No camera now, or the same one: the rate applies as it would on its own.
            if (fps is { } rate)
            {
                ApplyCameraFrameRate(rate);
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="device"/>, at <paramref name="fps"/>, the camera's stream. The new
    /// stream is built before the old one is taken down, so that if it cannot be, the camera stays
    /// as it was and the request is refused. That works for the same camera too, given its
    /// <paramref name="modes"/>: the new stream only opens it once the old one has let go of it.
    /// Under <c>_layoutLock</c>.
    /// </summary>
    private void ReplaceCamera(CameraDevice device, int fps, IReadOnlyList<CameraMode>? modes = null)
    {
        VideoPipeline created;
        try
        {
            created = CreateCameraPipeline(device, fps, modes);
        }
        catch (Exception ex)
        {
            string problem = $"{device.Name} cannot be used: {ex.Message}";
            Log.Warn(problem);
            if (_camera is null)
            {
                _cameraProblem = problem;
            }

            throw new RefusedException(problem, ex);
        }

        _camera?.Dispose();
        if (_started)
        {
            created.Start();
        }

        _camera = created;
        _cameraProblem = null;
        Publish();
    }

    /// <summary>
    /// The camera's stream failed (on one of its threads). It cannot be disposed from there, so
    /// the update thread drops it (<see cref="DropFailedCamera"/>); the screens carry on.
    /// </summary>
    private void OnCameraFaulted(VideoPipeline pipeline, Exception exception)
    {
        Log.Debug($"The camera's video failed: {exception}");
        Interlocked.Exchange(ref _failedCamera, new CameraFailure(pipeline, exception));
        OnStateChanged();
    }

    /// <summary>Takes down the camera's stream if it failed, saying why. Runs on the update thread only.</summary>
    private void DropFailedCamera()
    {
        if (Interlocked.Exchange(ref _failedCamera, null) is not { } failure)
        {
            return;
        }

        lock (_layoutLock)
        {
            // It may have been switched off or replaced since.
            if (_disposed || _camera != failure.Pipeline)
            {
                return;
            }

            _cameraProblem = $"{failure.Pipeline.Name} stopped: {failure.Error.Message}";
            Log.Warn($"{_cameraProblem} Carrying on without the camera.");
            failure.Pipeline.Dispose();
            _camera = null;
            Publish();
        }
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
                DropFailedCamera();
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
            string id = pipeline.Id;
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

    /// <summary>The camera's stream, and why it failed.</summary>
    private sealed record CameraFailure(VideoPipeline Pipeline, Exception Error);
}
