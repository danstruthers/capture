using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using Capture.Audio;
using Capture.Video;
using SIPSorcery.Media;
using SIPSorcery.Net;
using WebSocketSharp.Server;
using AppLog = Capture.Log;

namespace Capture.Output;

/// <summary>What viewers may ask the capture client to do, and what they are told about it.</summary>
internal interface IRemoteControl
{
    /// <summary>What the capture client is doing now.</summary>
    StateMessage GetState();

    /// <summary>Throws <see cref="RefusedException"/>, with a message for the person who asked, if it cannot be done.</summary>
    void Execute(CommandRequest command);

    /// <summary>Throws <see cref="RefusedException"/> likewise.</summary>
    void Configure(ConfigureRequest settings);
}

/// <param name="RemoteControl">Whether viewers may control the capture, or only watch.</param>
internal sealed record StreamSettings(IPAddress BindAddress, int Port, IReadOnlyList<string> AllowedOrigins, string? StunServer, H264Profile Profile, bool RemoteControl);

/// <summary>A video stream offered to viewers: a screen, or the whole desktop.</summary>
/// <param name="Number">Identifies the stream in binary messages: the screen number, or 0 for the desktop.</param>
internal sealed record StreamSource(string Id, int Number, Action RequestKeyFrame);

/// <summary>
/// The network side of the capture client: a WebSocket server that each viewer connects to (one
/// <see cref="ViewerConnection"/> each), the streaming state, and the video mode. The messages are
/// defined in <see cref="ViewerProtocol"/>.
/// <para>
/// Media reaches viewers in one of two ways. In H.264 mode each viewer gets a WebRTC session
/// (<see cref="WebRtcPeer"/>) with a track per stream; only the streams it selected are sent. In
/// JPEG mode nothing but the WebSocket is used: binary messages hold either a JPEG picture of one
/// stream or a block of 8 kHz PCM.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised, on whichever thread made the change, when the streaming state,
/// the video mode, the set of viewers or what they watch changes.
/// </para>
/// </summary>
internal sealed class StreamServer : IDisposable
{
    private readonly ConcurrentDictionary<ViewerConnection, byte> _connections = new();
    private readonly object _stateLock = new();
    private WebSocketServer? _server;
    private StreamingState _state;
    private VideoMode _mode;
    private volatile StreamSource[] _sources = [];

    public StreamServer(StreamSettings settings, IRemoteControl control, StreamingState initialState, VideoMode initialMode, bool hasAudio)
    {
        Settings = settings;
        Control = control;
        HasAudio = hasAudio;
        _state = initialState;
        _mode = initialMode;
    }

    /// <summary>Raised when the streaming state, the video mode, the set of viewers or what they watch changes.</summary>
    public event Action? Changed;

    public StreamingState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public VideoMode Mode
    {
        get
        {
            lock (_stateLock)
            {
                return _mode;
            }
        }
    }

    public int ClientCount => _connections.Count;

    /// <summary>Viewers being sent media right now.</summary>
    public int ViewerCount => _connections.Keys.Count(connection => connection.ReceivesAnything);

    public bool WantsAudio => HasAudio && _connections.Keys.Any(connection => connection.ReceivesAudio);

    internal StreamSettings Settings { get; }

    internal IRemoteControl Control { get; }

    internal bool HasAudio { get; }

    /// <summary>The video streams on offer.</summary>
    internal IReadOnlyList<StreamSource> Sources => _sources;

    /// <summary>Viewers receiving the given stream.</summary>
    public int ViewersOf(string sourceId) => _connections.Keys.Count(connection => connection.Receives(sourceId));

    public bool WantsH264(string sourceId) => Mode == VideoMode.H264 && _connections.Keys.Any(connection => connection.Receives(sourceId));

    public bool WantsJpeg(string sourceId) => Mode == VideoMode.Jpeg && _connections.Keys.Any(connection => connection.Receives(sourceId));

    public void Start()
    {
        SipSorceryLogging.Install();
        _server = new WebSocketServer(Settings.BindAddress, Settings.Port);

        // The server otherwise prints its own diagnostics (port probes, malformed requests) straight to the console.
        _server.Log.Output = (data, _) => AppLog.Debug($"[signaling] {data.Message}");

        _server.AddWebSocketService<ViewerSocket>("/", socket =>
        {
            socket.Server = this;
            socket.OriginValidator = CheckOrigin;
        });
        _server.Start();
    }

    /// <summary>
    /// Sets the video streams on offer. WebRTC sessions are set up again with one track per new
    /// stream; viewers keep watching the streams they selected that still exist.
    /// </summary>
    public void SetSources(IReadOnlyList<StreamSource> sources)
    {
        _sources = [.. sources];
        foreach (ViewerConnection connection in _connections.Keys)
        {
            connection.RestartPeer();
        }

        Changed?.Invoke();
    }

    public void StartStreaming() => SetState(StreamingState.Streaming, from: null);

    public void StopStreaming() => SetState(StreamingState.Stopped, from: null);

    public void PauseStreaming() =>
        SetState(StreamingState.Paused, from: null, refusalWhenStopped: "Streaming is stopped, so there is nothing to pause.");

    public void ResumeStreaming() =>
        SetState(StreamingState.Streaming, from: StreamingState.Paused, refusalWhenStopped: "Streaming is stopped. Start it instead of resuming.");

    public void SetMode(VideoMode mode)
    {
        lock (_stateLock)
        {
            if (_mode == mode)
            {
                return;
            }

            _mode = mode;
        }

        Reconcile();
        Changed?.Invoke();
    }

    public void SendVideo(string sourceId, EncodedVideoFrame frame)
    {
        if (State != StreamingState.Streaming || Mode != VideoMode.H264)
        {
            return;
        }

        foreach (ViewerConnection connection in _connections.Keys)
        {
            connection.SendVideo(sourceId, frame);
        }
    }

    public void SendPicture(string sourceId, JpegFrame frame)
    {
        if (State != StreamingState.Streaming || Mode != VideoMode.Jpeg || _connections.IsEmpty || Find(sourceId) is not { } source)
        {
            return;
        }

        byte[] message = ViewerProtocol.MediaMessage(ViewerProtocol.PictureMessage, source.Number, frame.Timestamp, frame.Data);
        foreach (ViewerConnection connection in _connections.Keys)
        {
            connection.QueuePicture(source, message);
        }
    }

    public void SendAudio(AudioFrame frame)
    {
        if (State != StreamingState.Streaming || _connections.IsEmpty)
        {
            return;
        }

        if (Mode == VideoMode.Jpeg)
        {
            byte[] message = ViewerProtocol.MediaMessage(ViewerProtocol.AudioMessage, 0, frame.Timestamp, MemoryMarshal.AsBytes(frame.Samples.AsSpan()));
            foreach (ViewerConnection connection in _connections.Keys)
            {
                connection.QueueAudio(message);
            }

            return;
        }

        byte[] muLaw = new byte[frame.Samples.Length];
        for (int i = 0; i < muLaw.Length; i++)
        {
            muLaw[i] = MuLawEncoder.LinearToMuLawSample(frame.Samples[i]);
        }

        foreach (ViewerConnection connection in _connections.Keys)
        {
            connection.SendAudio(muLaw, frame.Timestamp);
        }
    }

    /// <summary>Sends the current state to every viewer.</summary>
    public void BroadcastState()
    {
        if (_connections.IsEmpty)
        {
            return;
        }

        string json = ViewerProtocol.Serialize(Control.GetState());
        foreach (ViewerConnection connection in _connections.Keys)
        {
            connection.SendState(json);
        }
    }

    public void Dispose()
    {
        foreach (ViewerConnection connection in _connections.Keys)
        {
            connection.Close("capture stopped");
        }

        try
        {
            _server?.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Stopping the signaling server: {ex.Message}");
        }
    }

    /// <summary>
    /// A WebSocket is not covered by the browser's same-origin policy: any web page can open one to
    /// localhost. Checking the Origin header keeps arbitrary sites from watching or controlling
    /// the capture. Clients that send no Origin (anything that is not a browser) are not restricted by this.
    /// </summary>
    internal static bool IsOriginAllowed(string? origin, IReadOnlyList<string> allowedOrigins) =>
        string.IsNullOrEmpty(origin)
        || allowedOrigins.Contains("*")
        || allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
        || (Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && uri.IsLoopback);

    /// <summary>A newly connected viewer: it gets the state, and a WebRTC offer if streaming in H.264 mode.</summary>
    internal ViewerConnection Add(ViewerSocket socket)
    {
        var connection = new ViewerConnection(this, socket);
        _connections[connection] = 0;
        AppLog.Info($"Client {connection.Name} connected. Clients: {_connections.Count}");

        try
        {
            connection.SendState(ViewerProtocol.Serialize(Control.GetState()));
            connection.Reconcile();
        }
        catch
        {
            // Left registered, it would count as a viewer for ever and keep the screens captured.
            connection.Close("it could not be set up");
            throw;
        }

        Changed?.Invoke();
        return connection;
    }

    internal void Remove(ViewerConnection connection)
    {
        if (_connections.TryRemove(connection, out _))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>For a connection: something about what it receives has changed.</summary>
    internal void NotifyChanged() => Changed?.Invoke();

    /// <summary>A WebRTC session for a viewer, with a track for each of the current streams.</summary>
    internal WebRtcPeer CreatePeer(string viewer, Action<string> signal)
    {
        var configuration = new RTCConfiguration { X_UseRtpFeedbackProfile = true };
        if (Settings.StunServer is { } stun)
        {
            configuration.iceServers = [new RTCIceServer { urls = stun }];
        }

        // profile-level-id: the profile, with level 3.1. That level is lower than large streams
        // need, but browsers decode what they are sent whatever level was declared (the end-to-end
        // test plays a 4096x1316 desktop stream in Edge).
        string profile = Settings.Profile switch
        {
            H264Profile.High => "64001f",
            H264Profile.Main => "4d001f",
            _ => "42e01f",
        };

        return new WebRtcPeer(_sources, HasAudio, configuration, $"level-asymmetry-allowed=1;packetization-mode=1;profile-level-id={profile}", viewer, signal);
    }

    private StreamSource? Find(string sourceId) => Array.Find(_sources, source => source.Id == sourceId);

    /// <param name="from">Only change from this state (null: from any).</param>
    /// <param name="refusalWhenStopped">If set, streaming being stopped is a mistake: this is thrown. Checked under the lock, so a concurrent stop cannot slip in between.</param>
    private void SetState(StreamingState state, StreamingState? from, string? refusalWhenStopped = null)
    {
        lock (_stateLock)
        {
            if (refusalWhenStopped is not null && _state == StreamingState.Stopped)
            {
                throw new RefusedException(refusalWhenStopped);
            }

            if (_state == state || (from is not null && _state != from))
            {
                return;
            }

            _state = state;
        }

        Reconcile();
        if (state == StreamingState.Streaming)
        {
            // Viewers that sat through a pause need fresh key frames to start decoding again.
            foreach (ViewerConnection connection in _connections.Keys)
            {
                connection.WaitForKeyFrames();
            }

            foreach (StreamSource source in _sources)
            {
                source.RequestKeyFrame();
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Brings every viewer's WebRTC session in line with the state; one viewer's failure does not stop the others.</summary>
    private void Reconcile()
    {
        foreach (ViewerConnection connection in _connections.Keys)
        {
            connection.TryReconcile();
        }
    }

    private bool CheckOrigin(string? origin)
    {
        bool allowed = IsOriginAllowed(origin, Settings.AllowedOrigins);
        if (!allowed)
        {
            AppLog.Warn($"Rejected a client from origin {origin}. Pass --allow-origin {origin} to allow it.");
        }

        return allowed;
    }
}
