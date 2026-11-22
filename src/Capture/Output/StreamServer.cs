using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Capture.Audio;
using Capture.Video;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using WebSocketSharp;
using WebSocketSharp.Server;
using AppLog = Capture.Log;

namespace Capture.Output;

internal enum StreamingState
{
    Stopped,
    Streaming,
    Paused,
}

internal enum VideoMode
{
    /// <summary>H.264 video and G.711 audio over WebRTC.</summary>
    H264,

    /// <summary>JPEG pictures and raw PCM audio over the WebSocket itself.</summary>
    Jpeg,
}

/// <summary>What a connected client may ask the capture client to do.</summary>
internal interface IRemoteControl
{
    bool Enabled { get; }

    /// <summary>The current state as the JSON text of a "state" message.</summary>
    string GetStateJson();

    /// <summary>Runs start/stop/pause/resume on "recording" or "streaming". Throws <see cref="InvalidOperationException"/> with a message for the user if it cannot.</summary>
    void Execute(string target, string action);

    /// <summary>Applies settings such as frameRate, videoMode, audioSource and layout. Throws <see cref="InvalidOperationException"/> likewise.</summary>
    void Configure(JsonElement settings);
}

internal sealed record StreamSettings(IPAddress BindAddress, int Port, IReadOnlyList<string> AllowedOrigins, string? StunServer, H264Profile Profile);

/// <summary>A video stream offered to viewers: a screen, or the whole desktop.</summary>
/// <param name="Number">Identifies the stream in binary messages: the screen number, or 0 for the desktop.</param>
internal sealed record StreamSource(string Id, int Number, Action RequestKeyFrame);

/// <summary>
/// The network side of the capture client: a WebSocket server that each viewer connects to.
/// <para>
/// The socket carries JSON text messages in both directions. From here: "state" (what the capture
/// client is doing and which video streams it offers, sent on connect and on every change),
/// "error" (a refused request), and the WebRTC "offer" and ICE candidates. From the viewer:
/// "select" (which streams this viewer wants), "command" (start/stop/pause/resume of recording or
/// streaming), "configure" (frame rate, video mode, audio source, screen layout), and the WebRTC
/// "answer" and ICE candidates.
/// </para>
/// <para>
/// Media reaches viewers in one of two ways. In H.264 mode each viewer gets a WebRTC session
/// offered from this side, with one H.264 video track per stream and one G.711 mu-law audio track
/// (the WebRTC codec for 8 kHz PCM); only the streams the viewer selected are sent. In JPEG mode
/// nothing but the WebSocket is used: binary messages hold either a JPEG picture of one stream or a
/// block of 16-bit 8 kHz PCM, each prefixed by a type byte, a stream number and a 64-bit timestamp.
/// </para>
/// </summary>
internal sealed class StreamServer : IDisposable
{
    public const byte PictureMessage = 1;
    public const byte AudioMessage = 2;
    public const int MessageHeaderLength = 10;

    private const int FirstVideoPayloadType = 96;
    private const int TelephoneEventPayloadType = 101; // taken by the audio track
    private const int VideoClockRate = 90000;
    private const int MaxRtpPayload = 1200;
    private const int MaxQueuedAudioMessages = 50; // one second

    private readonly StreamSettings _settings;
    private readonly IRemoteControl _control;
    private readonly bool _hasAudio;
    private readonly ConcurrentDictionary<Client, byte> _clients = new();
    private readonly object _stateLock = new();
    private WebSocketServer? _server;
    private StreamingState _state;
    private VideoMode _mode;
    private volatile StreamSource[] _sources = [];

    public StreamServer(StreamSettings settings, IRemoteControl control, StreamingState initialState, VideoMode initialMode, bool hasAudio)
    {
        _settings = settings;
        _control = control;
        _state = initialState;
        _mode = initialMode;
        _hasAudio = hasAudio;
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

    public int ClientCount => _clients.Count;

    /// <summary>Clients that are being sent media right now.</summary>
    public int ViewerCount => _clients.Keys.Count(client => client.ReceivesAnything);

    public bool WantsAudio => _hasAudio && _clients.Keys.Any(client => client.ReceivesAudio);

    /// <summary>Clients receiving the given stream.</summary>
    public int ViewersOf(string sourceId) => _clients.Keys.Count(client => client.Receives(sourceId));

    public bool WantsH264(string sourceId) => Mode == VideoMode.H264 && _clients.Keys.Any(client => client.Receives(sourceId));

    public bool WantsJpeg(string sourceId) => Mode == VideoMode.Jpeg && _clients.Keys.Any(client => client.Receives(sourceId));

    public void Start()
    {
        _server = new WebSocketServer(_settings.BindAddress, _settings.Port);

        // The server otherwise prints its own diagnostics (port probes, malformed requests) straight to the console.
        _server.Log.Output = (data, _) => AppLog.Debug($"[signaling] {data.Message}");

        _server.AddWebSocketService<SignalingSession>("/", session =>
        {
            session.Server = this;
            session.OriginValidator = IsOriginAllowed;
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
        foreach (Client client in _clients.Keys)
        {
            client.RestartPeer();
        }

        Changed?.Invoke();
    }

    public void StartStreaming() => SetState(StreamingState.Streaming, from: null);

    public void StopStreaming() => SetState(StreamingState.Stopped, from: null);

    public void PauseStreaming()
    {
        if (State == StreamingState.Stopped)
        {
            throw new InvalidOperationException("Streaming is stopped, so there is nothing to pause.");
        }

        SetState(StreamingState.Paused, from: null);
    }

    public void ResumeStreaming()
    {
        if (State == StreamingState.Stopped)
        {
            throw new InvalidOperationException("Streaming is stopped. Start it instead of resuming.");
        }

        SetState(StreamingState.Streaming, from: StreamingState.Paused);
    }

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

        foreach (Client client in _clients.Keys)
        {
            client.SendVideo(sourceId, frame);
        }
    }

    public void SendPicture(string sourceId, JpegFrame frame)
    {
        if (State != StreamingState.Streaming || Mode != VideoMode.Jpeg || _clients.IsEmpty || Find(sourceId) is not { } source)
        {
            return;
        }

        byte[] message = BuildMessage(PictureMessage, source.Number, frame.Timestamp, frame.Data);
        foreach (Client client in _clients.Keys)
        {
            client.QueuePicture(source, message);
        }
    }

    public void SendAudio(AudioFrame frame)
    {
        if (State != StreamingState.Streaming || _clients.IsEmpty)
        {
            return;
        }

        if (Mode == VideoMode.Jpeg)
        {
            byte[] message = BuildMessage(AudioMessage, 0, frame.Timestamp, MemoryMarshal.AsBytes(frame.Samples.AsSpan()));
            foreach (Client client in _clients.Keys)
            {
                client.QueueAudio(message);
            }

            return;
        }

        byte[] muLaw = new byte[frame.Samples.Length];
        for (int i = 0; i < muLaw.Length; i++)
        {
            muLaw[i] = MuLawEncoder.LinearToMuLawSample(frame.Samples[i]);
        }

        foreach (Client client in _clients.Keys)
        {
            client.SendAudio(muLaw, frame.Timestamp);
        }
    }

    /// <summary>Sends the current state to every connected client.</summary>
    public void BroadcastState()
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        string json = _control.GetStateJson();
        foreach (Client client in _clients.Keys)
        {
            client.SendText(json);
        }
    }

    public void Dispose()
    {
        foreach (Client client in _clients.Keys)
        {
            client.Close("capture stopped");
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

    /// <summary>The H.264 payload type of each video section of an SDP description, by the section's media id (mid).</summary>
    internal static Dictionary<string, int> VideoPayloadTypesByMid(string sdp)
    {
        var result = new Dictionary<string, int>();
        bool video = false;
        string? mid = null;
        int? payloadType = null;

        void EndSection()
        {
            if (video && mid is not null && payloadType is not null)
            {
                result[mid] = payloadType.Value;
            }
        }

        foreach (string rawLine in sdp.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                EndSection();
                video = line.StartsWith("m=video", StringComparison.Ordinal);
                mid = null;
                payloadType = null;
            }
            else if (line.StartsWith("a=mid:", StringComparison.Ordinal))
            {
                mid = line["a=mid:".Length..].Trim();
            }
            else if (payloadType is null && line.StartsWith("a=rtpmap:", StringComparison.Ordinal) && line.Contains(" H264/", StringComparison.OrdinalIgnoreCase))
            {
                int space = line.IndexOf(' ');
                if (int.TryParse(line.AsSpan("a=rtpmap:".Length, space - "a=rtpmap:".Length), out int value))
                {
                    payloadType = value;
                }
            }
        }

        EndSection();
        return result;
    }

    /// <summary>[type][stream number][timestamp: int64 little-endian, 100 ns units][payload]</summary>
    private static byte[] BuildMessage(byte type, int number, long timestamp, ReadOnlySpan<byte> payload)
    {
        byte[] message = new byte[MessageHeaderLength + payload.Length];
        message[0] = type;
        message[1] = (byte)number;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(2), timestamp);
        payload.CopyTo(message.AsSpan(MessageHeaderLength));
        return message;
    }

    private static string ErrorJson(string message) => JsonSerializer.Serialize(new { type = "error", message });

    private static int NextPayloadType(int payloadType) => payloadType + 1 == TelephoneEventPayloadType ? payloadType + 2 : payloadType + 1;

    private StreamSource? Find(string sourceId) => Array.Find(_sources, source => source.Id == sourceId);

    private void SetState(StreamingState state, StreamingState? from)
    {
        lock (_stateLock)
        {
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
            foreach (Client client in _clients.Keys)
            {
                client.WaitForKeyFrames();
            }

            foreach (StreamSource source in _sources)
            {
                source.RequestKeyFrame();
            }
        }

        Changed?.Invoke();
    }

    private void Reconcile()
    {
        foreach (Client client in _clients.Keys)
        {
            client.Reconcile();
        }
    }

    /// <summary>
    /// A WebSocket is not covered by the browser's same-origin policy: any web page can open one to
    /// localhost. Checking the Origin header keeps arbitrary sites from watching or controlling
    /// the capture. Clients that send no Origin (anything that is not a browser) are not restricted by this.
    /// </summary>
    private bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || _settings.AllowedOrigins.Contains("*"))
        {
            return true;
        }

        if (_settings.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        bool allowed = Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
            && uri.IsLoopback;

        if (!allowed)
        {
            AppLog.Warn($"Rejected a client from origin {origin}. Pass --allow-origin {origin} to allow it.");
        }

        return allowed;
    }

    private string H264FormatParameters()
    {
        string profile = _settings.Profile switch
        {
            H264Profile.High => "64001f",
            H264Profile.Main => "4d001f",
            _ => "42e01f",
        };

        return $"level-asymmetry-allowed=1;packetization-mode=1;profile-level-id={profile}";
    }

    private RTCConfiguration PeerConfiguration()
    {
        var configuration = new RTCConfiguration { X_UseRtpFeedbackProfile = true };
        if (_settings.StunServer is { } stun)
        {
            configuration.iceServers = [new RTCIceServer { urls = stun }];
        }

        return configuration;
    }

    private Client AddClient(SignalingSession session)
    {
        var client = new Client(this, session);
        _clients[client] = 0;
        AppLog.Info($"Client {session.RemoteEndPoint} connected. Clients: {_clients.Count}");

        client.SendText(_control.GetStateJson());
        client.Reconcile();
        Changed?.Invoke();
        return client;
    }

    private void Remove(Client client)
    {
        if (_clients.TryRemove(client, out _))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>One WebSocket connection; created by the server for each client.</summary>
    internal sealed class SignalingSession : WebSocketBehavior
    {
        private Client? _client;

        public StreamServer Server { get; set; } = null!;

        public string RemoteEndPoint { get; private set; } = "client";

        public string? QueryValue(string name) => Context.QueryString[name];

        public void SendText(string text)
        {
            try
            {
                Context.WebSocket.Send(text);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Send to {RemoteEndPoint} failed: {ex.Message}");
            }
        }

        public bool SendBytes(byte[] data)
        {
            try
            {
                Context.WebSocket.Send(data);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Send to {RemoteEndPoint} failed: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                Context.WebSocket.Close();
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Closing the connection to {RemoteEndPoint}: {ex.Message}");
            }
        }

        protected override void OnOpen()
        {
            RemoteEndPoint = Context.UserEndPoint?.ToString() ?? "client";
            try
            {
                _client = Server.AddClient(this);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not set up client {RemoteEndPoint}: {ex.Message}");
                Disconnect();
            }
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (e.IsText)
            {
                _client?.HandleMessage(e.Data);
            }
        }

        protected override void OnClose(CloseEventArgs e) => _client?.Close("client disconnected");

        protected override void OnError(WebSocketSharp.ErrorEventArgs e) => AppLog.Debug($"WebSocket error for {RemoteEndPoint}: {e.Message}");
    }

    private sealed class Client
    {
        private readonly StreamServer _server;
        private readonly SignalingSession _session;
        private readonly object _peerLock = new();
        private readonly object _sendLock = new();
        private readonly object _selectionLock = new();
        private readonly ConcurrentQueue<byte[]> _audioMessages = new();
        private readonly ConcurrentDictionary<int, byte[]> _pictures = new();
        private readonly ManualResetEventSlim _binaryPending = new(false);
        private readonly Thread _binarySender;

        private PeerSession? _peer;
        private HashSet<string>? _selection; // null: every stream
        private volatile bool _wantsAudio = true;
        private volatile bool _closed;

        public Client(StreamServer server, SignalingSession session)
        {
            _server = server;
            _session = session;

            // A viewer can say up front what it wants (?select=screen2&audio=0), so it is never sent anything else.
            if (session.QueryValue("select") is { } select)
            {
                _selection = [.. select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            }

            if (session.QueryValue("audio") is "0" or "false")
            {
                _wantsAudio = false;
            }

            _binarySender = new Thread(BinarySendLoop) { IsBackground = true, Name = "websocket-media-sender" };
            _binarySender.Start();
        }

        /// <summary>Whether the client is being sent media at all: streaming, and a WebRTC session up or JPEG mode.</summary>
        private bool MediaFlowing =>
            !_closed && _server.State == StreamingState.Streaming && (_server.Mode == VideoMode.Jpeg || _peer is { Connected: true });

        public bool ReceivesAudio => MediaFlowing && _wantsAudio;

        public bool ReceivesAnything => MediaFlowing && (_wantsAudio || _server._sources.Any(source => IsSelected(source.Id)));

        public bool Receives(string sourceId) => MediaFlowing && IsSelected(sourceId);

        public void SendText(string text)
        {
            if (!_closed)
            {
                _session.SendText(text);
            }
        }

        /// <summary>Starts or ends this client's WebRTC session so that it matches the streaming state and video mode.</summary>
        public void Reconcile()
        {
            bool wantPeer = !_closed && _server.State != StreamingState.Stopped && _server.Mode == VideoMode.H264;
            PeerSession? ended = null;

            lock (_peerLock)
            {
                if (wantPeer && _peer is null)
                {
                    StartPeerLocked();
                }
                else if (!wantPeer && _peer is not null)
                {
                    ended = _peer;
                    _peer = null;
                }
            }

            if (ended is not null)
            {
                ClosePeer(ended, "streaming stopped");
                _server.Changed?.Invoke();
            }
        }

        /// <summary>Replaces the WebRTC session with one that has a track for each of the current streams.</summary>
        public void RestartPeer()
        {
            PeerSession? ended;
            lock (_peerLock)
            {
                ended = _peer;
                _peer = null;
            }

            if (ended is not null)
            {
                ClosePeer(ended, "the video streams changed");
            }

            Reconcile();
        }

        public void WaitForKeyFrames()
        {
            if (_peer is not { } peer)
            {
                return;
            }

            lock (_sendLock)
            {
                foreach (PeerVideo video in peer.Videos.Values)
                {
                    video.WaitingForKeyFrame = true;
                }
            }
        }

        public void HandleMessage(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                string? type = root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;

                if (type == "select")
                {
                    HandleSelect(root);
                }
                else if (type is "command" or "configure")
                {
                    HandleControl(type, root);
                }
                else if (root.TryGetProperty("candidate", out JsonElement candidate))
                {
                    // An empty candidate string is the browser's end-of-candidates marker.
                    if (!string.IsNullOrWhiteSpace(candidate.GetString()) && RTCIceCandidateInit.TryParse(json, out RTCIceCandidateInit? init))
                    {
                        _peer?.Connection.addIceCandidate(init);
                    }
                }
                else if (root.TryGetProperty("sdp", out _) && RTCSessionDescriptionInit.TryParse(json, out RTCSessionDescriptionInit? description))
                {
                    HandleAnswer(description);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Ignored a message from {_session.RemoteEndPoint}: {ex.Message}");
            }
        }

        public void SendVideo(string sourceId, EncodedVideoFrame frame)
        {
            if (_peer is not { Connected: true } peer || !IsSelected(sourceId) || !peer.Videos.TryGetValue(sourceId, out PeerVideo? video) || video.Stream is null)
            {
                return;
            }

            try
            {
                lock (_sendLock)
                {
                    if (video.WaitingForKeyFrame)
                    {
                        if (!frame.IsKeyFrame)
                        {
                            return; // a decoder cannot start in the middle of a group of pictures
                        }

                        video.WaitingForKeyFrame = false;
                    }

                    // RTP time is the capture time (100 ns units -> 90 kHz), so it stays true to the
                    // capture clock across late frames, dropped frames and pauses.
                    uint timestamp = unchecked(video.TimestampBase + (uint)(frame.Timestamp * 9 / 1000));
                    SendH264(video, frame.Data, timestamp);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Video send to {_session.RemoteEndPoint} failed: {ex.Message}");
            }
        }

        public void SendAudio(byte[] muLaw, long captureTime)
        {
            if (!_wantsAudio || _peer is not { Connected: true } peer)
            {
                return;
            }

            try
            {
                lock (_sendLock)
                {
                    // G.711 runs at 8 kHz: 100 ns units -> samples.
                    uint timestamp = unchecked(peer.AudioTimestampBase + (uint)(captureTime * 8 / 10_000));
                    peer.Connection.SendRtpRaw(SDPMediaTypesEnum.audio, muLaw, timestamp, 0, peer.AudioPayloadType);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Audio send to {_session.RemoteEndPoint} failed: {ex.Message}");
            }
        }

        /// <summary>Offers a picture to this client. Only the newest one of each stream waits: a slow client gets fewer pictures, not older ones.</summary>
        public void QueuePicture(StreamSource source, byte[] message)
        {
            if (!IsSelected(source.Id))
            {
                return;
            }

            _pictures[source.Number] = message;
            _binaryPending.Set();
        }

        public void QueueAudio(byte[] message)
        {
            if (!_wantsAudio)
            {
                return;
            }

            _audioMessages.Enqueue(message);
            while (_audioMessages.Count > MaxQueuedAudioMessages)
            {
                _audioMessages.TryDequeue(out _);
            }

            _binaryPending.Set();
        }

        public void Close(string reason)
        {
            PeerSession? peer;
            lock (_peerLock)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                peer = _peer;
                _peer = null;
            }

            _binaryPending.Set();
            _server.Remove(this);

            if (peer is not null)
            {
                ClosePeer(peer, reason);
            }

            _session.Disconnect();
            AppLog.Info($"Client {_session.RemoteEndPoint} left ({reason}). Clients: {_server.ClientCount}");
        }

        private bool IsSelected(string sourceId)
        {
            lock (_selectionLock)
            {
                return _selection is null || _selection.Contains(sourceId);
            }
        }

        /// <summary>{"type":"select","sources":["screen1",...],"audio":true}: what this viewer wants to be sent.</summary>
        private void HandleSelect(JsonElement root)
        {
            HashSet<string>? selection = null;
            if (root.TryGetProperty("sources", out JsonElement sources) && sources.ValueKind == JsonValueKind.Array)
            {
                selection = [.. sources.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)];
            }

            bool wantsAudio = !root.TryGetProperty("audio", out JsonElement audio) || audio.ValueKind != JsonValueKind.False;

            List<StreamSource> added;
            lock (_selectionLock)
            {
                added = [.. _server._sources.Where(source => !(_selection?.Contains(source.Id) ?? true) && (selection?.Contains(source.Id) ?? true))];
                _selection = selection;
            }

            _wantsAudio = wantsAudio;

            // A stream this viewer has not been receiving can only be decoded from a key frame.
            if (_peer is { } peer)
            {
                lock (_sendLock)
                {
                    foreach (StreamSource source in added)
                    {
                        if (peer.Videos.TryGetValue(source.Id, out PeerVideo? video))
                        {
                            video.WaitingForKeyFrame = true;
                        }
                    }
                }
            }

            foreach (StreamSource source in added)
            {
                source.RequestKeyFrame();
            }

            AppLog.Debug($"Client {_session.RemoteEndPoint} selected {(selection is null ? "every stream" : selection.Count == 0 ? "no streams" : string.Join(", ", selection))}{(wantsAudio ? " with audio" : " without audio")}.");
            _server.Changed?.Invoke();
        }

        private void HandleControl(string type, JsonElement root)
        {
            if (!_server._control.Enabled)
            {
                SendText(ErrorJson("Remote control is turned off on the capture client."));
                return;
            }

            try
            {
                if (type == "command")
                {
                    string target = root.GetProperty("target").GetString() ?? string.Empty;
                    string action = root.GetProperty("action").GetString() ?? string.Empty;
                    AppLog.Info($"Remote control from {_session.RemoteEndPoint}: {action} {target}");
                    _server._control.Execute(target, action);
                }
                else
                {
                    AppLog.Info($"Remote control from {_session.RemoteEndPoint}: {root.GetRawText()}");
                    _server._control.Configure(root);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                AppLog.Warn($"Remote control request refused: {ex.Message}");
                SendText(ErrorJson(ex.Message));
            }
        }

        private void StartPeerLocked()
        {
            var connection = new RTCPeerConnection(_server.PeerConfiguration());
            var videos = new Dictionary<string, PeerVideo>();
            var sourceByPayloadType = new Dictionary<int, string>();

            // One video track per stream. Each gets its own payload type, which is what ties the
            // browser's tracks (known to it by mid) back to the streams.
            int payloadType = FirstVideoPayloadType;
            foreach (StreamSource source in _server._sources)
            {
                var format = new VideoFormat(VideoCodecsEnum.H264, payloadType, VideoClockRate, _server.H264FormatParameters());
                var track = new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly);
                connection.addTrack(track);
                videos[source.Id] = new PeerVideo(source, track, payloadType);
                sourceByPayloadType[payloadType] = source.Id;
                payloadType = NextPayloadType(payloadType);
            }

            if (_server._hasAudio)
            {
                connection.addTrack(new MediaStreamTrack(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU), MediaStreamStatusEnum.SendOnly));
            }

            var peer = new PeerSession(connection, videos);
            _peer = peer;

            connection.onicecandidate += candidate =>
            {
                if (candidate is null)
                {
                    return;
                }

                // Candidates can be gathered before the offer has gone out; a browser rejects a
                // candidate that arrives ahead of the offer it belongs to.
                lock (peer.EarlyCandidates)
                {
                    if (peer.OfferSent)
                    {
                        _session.SendText(candidate.toJSON());
                    }
                    else
                    {
                        peer.EarlyCandidates.Add(candidate.toJSON());
                    }
                }
            };

            connection.onconnectionstatechange += state => OnConnectionStateChanged(peer, state);
            connection.OnReceiveReportByIndex += (_, _, media, report) => OnRtcpReport(peer, media, report);

            RTCSessionDescriptionInit offer = connection.createOffer();
            connection.setLocalDescription(offer).GetAwaiter().GetResult();
            AppLog.Debug($"SDP offer for {_session.RemoteEndPoint}:\n{offer.sdp}");

            Dictionary<string, string> sourceByMid = [];
            foreach ((string mid, int pt) in VideoPayloadTypesByMid(offer.sdp))
            {
                if (sourceByPayloadType.TryGetValue(pt, out string? sourceId))
                {
                    sourceByMid[mid] = sourceId;
                }
            }

            lock (peer.EarlyCandidates)
            {
                _session.SendText(JsonSerializer.Serialize(new { type = "offer", sdp = offer.sdp, sources = sourceByMid }));
                peer.OfferSent = true;
                foreach (string candidate in peer.EarlyCandidates)
                {
                    _session.SendText(candidate);
                }

                peer.EarlyCandidates.Clear();
            }
        }

        private void HandleAnswer(RTCSessionDescriptionInit description)
        {
            if (_peer is not { } peer)
            {
                return;
            }

            AppLog.Debug($"SDP {description.type} from {_session.RemoteEndPoint}:\n{description.sdp}");
            SetDescriptionResultEnum result = peer.Connection.setRemoteDescription(description);
            if (result != SetDescriptionResultEnum.OK)
            {
                AppLog.Warn($"Client {_session.RemoteEndPoint} sent an SDP answer that cannot be used ({result}).");
                SendText(ErrorJson($"The capture client could not use this browser's WebRTC answer ({result}). Try the JPEG video mode."));
            }
        }

        private void OnConnectionStateChanged(PeerSession peer, RTCPeerConnectionState state)
        {
            if (!ReferenceEquals(_peer, peer))
            {
                return;
            }

            AppLog.Debug($"Client {_session.RemoteEndPoint}: WebRTC {state}");

            if (state == RTCPeerConnectionState.connected && !peer.Connected)
            {
                lock (_sendLock)
                {
                    foreach (PeerVideo video in peer.Videos.Values)
                    {
                        video.Stream = peer.Connection.VideoStreamList.Find(stream => ReferenceEquals(stream.LocalTrack, video.Track));
                        video.PayloadType = video.Stream?.GetSendingFormat().ID ?? video.PayloadType;
                        video.WaitingForKeyFrame = true;
                    }

                    peer.AudioPayloadType = _server._hasAudio ? peer.Connection.AudioStream?.GetSendingFormat().ID ?? 0 : 0;
                }

                peer.Connected = true;
                AppLog.Info($"Client {_session.RemoteEndPoint} is receiving the WebRTC stream.");
                _server.Changed?.Invoke();

                // So the new viewer has pictures straight away.
                foreach (PeerVideo video in peer.Videos.Values)
                {
                    if (IsSelected(video.Source.Id))
                    {
                        video.Source.RequestKeyFrame();
                    }
                }
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                bool wasConnected;
                lock (_peerLock)
                {
                    if (!ReferenceEquals(_peer, peer))
                    {
                        return;
                    }

                    wasConnected = peer.Connected;
                    _peer = null;
                }

                if (state == RTCPeerConnectionState.failed)
                {
                    AppLog.Warn($"The WebRTC connection to {_session.RemoteEndPoint} failed.");
                    SendText(ErrorJson("The WebRTC connection failed. Try the JPEG video mode."));
                }

                if (wasConnected)
                {
                    _server.Changed?.Invoke();
                }
            }
        }

        /// <summary>A viewer that lost packets asks for a new key frame with an RTCP PLI or FIR.</summary>
        private void OnRtcpReport(PeerSession peer, SDPMediaTypesEnum media, RTCPCompoundPacket report)
        {
            if (media != SDPMediaTypesEnum.video || report.Feedback is not { } feedback)
            {
                return;
            }

            if (feedback.Header.PacketType != RTCPReportTypesEnum.PSFB
                || feedback.Header.PayloadFeedbackMessageType is not (PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR))
            {
                return;
            }

            // The feedback names the stream by its SSRC; if it cannot be matched, refresh them all.
            PeerVideo[] videos = [.. peer.Videos.Values];
            PeerVideo[] matched = [.. videos.Where(video => video.Track.Ssrc == feedback.MediaSSRC)];
            foreach (PeerVideo video in matched.Length > 0 ? matched : videos)
            {
                AppLog.Debug($"Client {_session.RemoteEndPoint} requested a key frame of {video.Source.Id}.");
                video.Source.RequestKeyFrame();
            }
        }

        private void ClosePeer(PeerSession peer, string reason)
        {
            try
            {
                peer.Connection.Close(reason);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Closing the WebRTC connection to {_session.RemoteEndPoint}: {ex.Message}");
            }
        }

        /// <summary>
        /// Packs an Annex B access unit into RTP packets (RFC 6184): a NAL unit that fits goes out as
        /// it is, a larger one is split into FU-A fragments. The marker bit ends the access unit.
        /// </summary>
        private static void SendH264(PeerVideo video, byte[] accessUnit, uint timestamp)
        {
            VideoStream stream = video.Stream!;
            List<AnnexB.Nal> nals = AnnexB.FindNals(accessUnit);
            for (int n = 0; n < nals.Count; n++)
            {
                AnnexB.Nal nal = nals[n];
                bool lastNal = n == nals.Count - 1;

                if (nal.Length <= MaxRtpPayload)
                {
                    byte[] packet = accessUnit.AsSpan(nal.Offset, nal.Length).ToArray();
                    stream.SendRtpRaw(packet, timestamp, lastNal ? 1 : 0, video.PayloadType);
                    continue;
                }

                byte header = accessUnit[nal.Offset];
                byte indicator = (byte)((header & 0xE0) | 28); // FU-A, keeping the NAL's importance bits
                int type = header & 0x1F;
                int offset = nal.Offset + 1;
                int remaining = nal.Length - 1;
                bool first = true;

                while (remaining > 0)
                {
                    int size = Math.Min(remaining, MaxRtpPayload - 2);
                    bool last = size == remaining;

                    byte[] packet = new byte[size + 2];
                    packet[0] = indicator;
                    packet[1] = (byte)(type | (first ? 0x80 : 0) | (last ? 0x40 : 0));
                    Buffer.BlockCopy(accessUnit, offset, packet, 2, size);
                    stream.SendRtpRaw(packet, timestamp, lastNal && last ? 1 : 0, video.PayloadType);

                    offset += size;
                    remaining -= size;
                    first = false;
                }
            }
        }

        /// <summary>Writes this client's queued pictures and audio to its socket, so one slow client never holds up another.</summary>
        private void BinarySendLoop()
        {
            while (!_closed)
            {
                _binaryPending.Wait();
                _binaryPending.Reset();

                while (!_closed && _audioMessages.TryDequeue(out byte[]? audio))
                {
                    if (!_session.SendBytes(audio))
                    {
                        return;
                    }
                }

                foreach (int number in _pictures.Keys)
                {
                    if (!_closed && _pictures.TryRemove(number, out byte[]? picture) && !_session.SendBytes(picture))
                    {
                        return;
                    }
                }
            }
        }
    }

    /// <summary>One viewer's WebRTC session.</summary>
    private sealed class PeerSession(RTCPeerConnection connection, Dictionary<string, PeerVideo> videos)
    {
        public RTCPeerConnection Connection { get; } = connection;

        /// <summary>The video track of each stream, by stream id.</summary>
        public Dictionary<string, PeerVideo> Videos { get; } = videos;

        public List<string> EarlyCandidates { get; } = [];

        public bool OfferSent { get; set; }

        public volatile bool Connected;

        public int AudioPayloadType { get; set; }

        public uint AudioTimestampBase { get; } = (uint)Random.Shared.Next();
    }

    /// <summary>One stream's video track within a viewer's WebRTC session.</summary>
    private sealed class PeerVideo(StreamSource source, MediaStreamTrack track, int payloadType)
    {
        public StreamSource Source { get; } = source;

        public MediaStreamTrack Track { get; } = track;

        public VideoStream? Stream { get; set; }

        public int PayloadType { get; set; } = payloadType;

        public uint TimestampBase { get; } = (uint)Random.Shared.Next();

        public bool WaitingForKeyFrame { get; set; } = true;
    }
}
