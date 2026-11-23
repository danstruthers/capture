using System.Collections.Concurrent;
using System.Text.Json;
using Capture.Video;
using SIPSorcery.Net;
using WebSocketSharp;
using WebSocketSharp.Server;
using AppLog = Capture.Log;

namespace Capture.Output;

/// <summary>
/// One viewer: what it wants to be sent, its WebRTC session (H.264 mode), and the messages waiting
/// to go to it.
/// <para>
/// Threads: the viewer's messages arrive on WebSocketSharp's receive thread; video and audio come
/// from the encoder and mixer threads; WebRTC events from SIPSorcery's threads; state changes from
/// the capture session. <c>_peerLock</c> guards starting, replacing and closing the WebRTC session
/// (<c>_peer</c>, <c>_closed</c>); <c>_peer</c> is read without it, so a reader takes the value once
/// and works with that session, which may have been closed meanwhile. <c>_selectionLock</c> guards
/// which streams the viewer wants. Only the sender thread writes to the socket; everything else
/// queues.
/// </para>
/// </summary>
internal sealed class ViewerConnection
{
    /// <summary>One second of PCM audio messages (JPEG mode) may wait for a slow viewer; older ones are dropped.</summary>
    private const int MaxQueuedAudioMessages = 50;

    private readonly StreamServer _server;
    private readonly ViewerSocket _socket;
    private readonly object _peerLock = new();
    private readonly object _selectionLock = new();

    private readonly ConcurrentQueue<string> _textMessages = new();
    private readonly ConcurrentQueue<byte[]> _audioMessages = new();
    private readonly ConcurrentDictionary<int, byte[]> _pictures = new();
    private readonly ManualResetEventSlim _sendPending = new(false);
    private string? _pendingState;

    private volatile WebRtcPeer? _peer;
    private IReadOnlySet<string>? _selection; // null: every stream
    private volatile bool _wantsAudio = true;
    private volatile bool _closed;

    public ViewerConnection(StreamServer server, ViewerSocket socket)
    {
        _server = server;
        _socket = socket;

        // A viewer can say up front what it wants (?select=screen2&audio=0), so it is never sent anything else.
        if (socket.QueryValue("select") is { } select)
        {
            _selection = new HashSet<string>(select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (socket.QueryValue("audio") is "0" or "false")
        {
            _wantsAudio = false;
        }

        // Everything goes out on this connection's own thread, so that one slow viewer never holds
        // up another, nor the thread that wanted to send.
        new Thread(SendLoop) { IsBackground = true, Name = "websocket-sender" }.Start();
    }

    public string Name => _socket.RemoteEndPoint;

    public bool ReceivesAudio => MediaFlowing && _wantsAudio;

    public bool ReceivesAnything => MediaFlowing && (_wantsAudio || _server.Sources.Any(source => IsSelected(source.Id)));

    /// <summary>Whether the viewer is being sent media at all: streaming, and in JPEG mode or with a WebRTC session up.</summary>
    private bool MediaFlowing =>
        !_closed && _server.State == StreamingState.Streaming && (_server.Mode == VideoMode.Jpeg || _peer is { IsConnected: true });

    public bool Receives(string sourceId) => MediaFlowing && IsSelected(sourceId);

    /// <summary>Queues a text message; text messages go out in order.</summary>
    public void SendText(string text)
    {
        if (!_closed)
        {
            _textMessages.Enqueue(text);
            _sendPending.Set();
        }
    }

    /// <summary>Queues the state. Only the newest waiting state goes out: a slow viewer skips states rather than falling behind.</summary>
    public void SendState(string json)
    {
        if (!_closed)
        {
            Volatile.Write(ref _pendingState, json);
            _sendPending.Set();
        }
    }

    /// <summary>Offers a picture. Only the newest one of each stream waits: a slow viewer gets fewer pictures, not older ones.</summary>
    public void QueuePicture(StreamSource source, byte[] message)
    {
        if (IsSelected(source.Id))
        {
            _pictures[source.Number] = message;
            _sendPending.Set();
        }
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

        _sendPending.Set();
    }

    public void SendVideo(string sourceId, EncodedVideoFrame frame)
    {
        if (IsSelected(sourceId))
        {
            _peer?.SendVideo(sourceId, frame);
        }
    }

    public void SendAudio(byte[] muLaw, long captureTime)
    {
        if (_wantsAudio)
        {
            _peer?.SendAudio(muLaw, captureTime);
        }
    }

    /// <summary>After a pause: every stream starts again from a key frame.</summary>
    public void WaitForKeyFrames() => _peer?.WaitForKeyFrames();

    /// <summary><see cref="Reconcile"/>, telling the viewer rather than the caller if its WebRTC session cannot be set up.</summary>
    public void TryReconcile()
    {
        try
        {
            Reconcile();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set up WebRTC for {Name}: {ex.Message}");
            SendText(ViewerProtocol.Error($"The capture client could not set up WebRTC ({ex.Message}). Try the JPEG video mode."));
        }
    }

    /// <summary>Starts or ends this viewer's WebRTC session so that it matches the streaming state and video mode.</summary>
    public void Reconcile()
    {
        WebRtcPeer? ended = null;
        lock (_peerLock)
        {
            // Decided under the lock, so that a Close in between cannot leave a new session behind on a closed connection.
            bool wantPeer = !_closed && _server.State != StreamingState.Stopped && _server.Mode == VideoMode.H264;
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
            ended.Close("streaming stopped");
            _server.NotifyChanged();
        }
    }

    /// <summary>Replaces the WebRTC session with one that has a track for each of the current streams.</summary>
    public void RestartPeer()
    {
        WebRtcPeer? ended;
        lock (_peerLock)
        {
            ended = _peer;
            _peer = null;
        }

        ended?.Close("the video streams changed");
        TryReconcile();
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

            string? type = ViewerProtocol.TypeOf(root);
            if (type == "select")
            {
                HandleSelect(SelectRequest.Parse(root));
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
                    _peer?.AddIceCandidate(init);
                }
            }
            else if (root.TryGetProperty("sdp", out _) && RTCSessionDescriptionInit.TryParse(json, out RTCSessionDescriptionInit? answer))
            {
                HandleAnswer(answer);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignored a message from {Name}: {ex.Message}");
        }
    }

    public void Close(string reason)
    {
        WebRtcPeer? peer;
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

        _sendPending.Set(); // ends the sender thread
        _server.Remove(this);
        peer?.Close(reason);
        _socket.Disconnect();
        Log.Info($"Client {Name} left ({reason}). Clients: {_server.ClientCount}");
    }

    private bool IsSelected(string sourceId)
    {
        lock (_selectionLock)
        {
            return _selection is null || _selection.Contains(sourceId);
        }
    }

    /// <summary>The session is set up before its offer goes out, so that the answer always finds it. Called holding _peerLock.</summary>
    private void StartPeerLocked()
    {
        WebRtcPeer peer = _server.CreatePeer(Name, SendText);
        peer.Connected += OnPeerConnected;
        peer.Ended += OnPeerEnded;
        _peer = peer;
        try
        {
            peer.Offer();
        }
        catch
        {
            _peer = null; // the peer has closed itself
            throw;
        }
    }

    private void OnPeerConnected(WebRtcPeer peer)
    {
        if (!ReferenceEquals(_peer, peer))
        {
            return;
        }

        Log.Info($"Client {Name} is receiving the WebRTC stream.");
        _server.NotifyChanged();

        // So that the new viewer has pictures straight away.
        foreach (StreamSource source in _server.Sources)
        {
            if (IsSelected(source.Id))
            {
                source.RequestKeyFrame();
            }
        }
    }

    /// <summary>The connection failed or closed by itself (a session this side replaced or closed is no longer <c>_peer</c>).</summary>
    private void OnPeerEnded(WebRtcPeer peer, bool failed)
    {
        bool wasConnected;
        lock (_peerLock)
        {
            if (!ReferenceEquals(_peer, peer))
            {
                return;
            }

            wasConnected = peer.IsConnected;
            _peer = null;
        }

        // There is no automatic retry: the viewer is told, and a new session is offered at the next
        // change of streaming state, video mode or layout (or when the viewer reconnects).
        peer.Close(failed ? "the connection failed" : "the connection closed");
        if (failed)
        {
            Log.Warn($"The WebRTC connection to {Name} failed.");
            SendText(ViewerProtocol.Error("The WebRTC connection failed. Try the JPEG video mode."));
        }

        if (wasConnected)
        {
            _server.NotifyChanged();
        }
    }

    private void HandleAnswer(RTCSessionDescriptionInit answer)
    {
        if (_peer is { } peer && peer.Answer(answer) is var result && result != SetDescriptionResultEnum.OK)
        {
            Log.Warn($"Client {Name} sent an SDP answer that cannot be used ({result}).");
            SendText(ViewerProtocol.Error($"The capture client could not use this browser's WebRTC answer ({result}). Try the JPEG video mode."));
        }
    }

    /// <summary>What this viewer wants to be sent from now on.</summary>
    private void HandleSelect(SelectRequest request)
    {
        List<StreamSource> added;
        lock (_selectionLock)
        {
            added = [.. _server.Sources.Where(source => !(_selection?.Contains(source.Id) ?? true) && (request.Sources?.Contains(source.Id) ?? true))];
            _selection = request.Sources;
        }

        _wantsAudio = request.Audio;

        // A stream this viewer has not been receiving can only be decoded from a key frame.
        foreach (StreamSource source in added)
        {
            _peer?.WaitForKeyFrame(source.Id);
            source.RequestKeyFrame();
        }

        string streams = request.Sources is null ? "every stream" : request.Sources.Count == 0 ? "no streams" : string.Join(", ", request.Sources);
        Log.Debug($"Client {Name} selected {streams}{(request.Audio ? " with audio" : " without audio")}.");
        _server.NotifyChanged();
    }

    /// <summary>
    /// Runs a "command" or "configure" request. The viewer always hears back about a request that
    /// did not work: with the reason when it was refused, otherwise with what went wrong.
    /// </summary>
    private void HandleControl(string type, JsonElement root)
    {
        JsonElement? request = root.TryGetProperty("id", out JsonElement id) ? id : null;
        if (!_server.Settings.RemoteControl)
        {
            SendText(ViewerProtocol.Error("Remote control is turned off on the capture client.", request));
            return;
        }

        try
        {
            if (type == "command")
            {
                CommandRequest command = CommandRequest.Parse(root);
                Log.Info($"Remote control from {Name}: {command}");
                _server.Control.Execute(command);
            }
            else
            {
                Log.Info($"Remote control from {Name}: {root.GetRawText()}");
                _server.Control.Configure(ConfigureRequest.Parse(root));
            }
        }
        catch (RefusedException ex)
        {
            Log.Warn($"Remote control request refused: {ex.Message}");
            SendText(ViewerProtocol.Error(ex.Message, request));
        }
        catch (Exception ex)
        {
            Log.Error($"Remote control request failed: {ex.Message}");
            Log.Debug(ex.ToString());
            SendText(ViewerProtocol.Error($"The capture client could not do that: {ex.Message}", request));
        }
    }

    /// <summary>
    /// Writes everything queued to the socket: the newest state, then text messages in order
    /// (offers, ICE candidates, errors), then audio, then the newest picture of each stream.
    /// </summary>
    private void SendLoop()
    {
        while (!_closed)
        {
            _sendPending.Wait();
            _sendPending.Reset();

            if (Interlocked.Exchange(ref _pendingState, null) is { } state && !_socket.SendText(state))
            {
                return;
            }

            while (!_closed && _textMessages.TryDequeue(out string? text))
            {
                if (!_socket.SendText(text))
                {
                    return;
                }
            }

            while (!_closed && _audioMessages.TryDequeue(out byte[]? audio))
            {
                if (!_socket.SendBytes(audio))
                {
                    return;
                }
            }

            foreach (int number in _pictures.Keys)
            {
                if (!_closed && _pictures.TryRemove(number, out byte[]? picture) && !_socket.SendBytes(picture))
                {
                    return;
                }
            }
        }
    }
}

/// <summary>The WebSocket of one viewer. WebSocketSharp makes one for each connection; it hands everything to its <see cref="ViewerConnection"/>.</summary>
internal sealed class ViewerSocket : WebSocketBehavior
{
    private ViewerConnection? _connection;

    public StreamServer Server { get; set; } = null!;

    public string RemoteEndPoint { get; private set; } = "client";

    public string? QueryValue(string name) => Context.QueryString[name];

    /// <summary>Sends now, on the calling thread; only the connection's sender thread calls this.</summary>
    public bool SendText(string text)
    {
        try
        {
            Context.WebSocket.Send(text);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Send to {RemoteEndPoint} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sends now, on the calling thread; only the connection's sender thread calls this.</summary>
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
            _connection = Server.Add(this);
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
            _connection?.HandleMessage(e.Data);
        }
    }

    protected override void OnClose(CloseEventArgs e) => _connection?.Close("client disconnected");

    protected override void OnError(WebSocketSharp.ErrorEventArgs e) => AppLog.Debug($"WebSocket error for {RemoteEndPoint}: {e.Message}");
}
