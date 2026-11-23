using Capture.Audio;
using Capture.Video;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Capture.Output;

/// <summary>
/// One viewer's WebRTC session, offered from this side: one H.264 video track per stream, and a
/// G.711 mu-law audio track (the WebRTC codec for 8 kHz PCM). Each video track gets its own payload
/// type, which is what ties the browser's tracks (known to it by mid) back to the streams.
/// <para>
/// Threads: SIPSorcery raises connection events and RTCP reports on its own threads; video and
/// audio come from the encoder and mixer threads. <c>_sendLock</c> lets one RTP send through at a
/// time and guards the tracks' waiting-for-a-key-frame flags. The ICE candidates gathered before
/// the offer has gone out wait in <c>_earlyCandidates</c>, which also guards <c>_offerSent</c>.
/// </para>
/// </summary>
internal sealed class WebRtcPeer
{
    private const int FirstVideoPayloadType = 96;
    private const int TelephoneEventPayloadType = 101; // taken by the audio track
    private const int VideoClockRate = 90000;

    private readonly RTCPeerConnection _connection;
    private readonly Dictionary<string, VideoTrack> _videos = []; // by stream id
    private readonly Dictionary<int, string> _sourceByPayloadType = [];
    private readonly bool _hasAudio;
    private readonly string _viewer;
    private readonly Action<string> _signal;
    private readonly object _sendLock = new();
    private readonly List<string> _earlyCandidates = [];
    private readonly uint _audioTimestampBase = (uint)Random.Shared.Next();
    private bool _offerSent;
    private int _audioPayloadType;
    private volatile bool _connected;
    private int _ended;

    /// <param name="viewer">Names the viewer in the log.</param>
    /// <param name="signal">Sends a text message (the offer, an ICE candidate) to the viewer.</param>
    public WebRtcPeer(IReadOnlyList<StreamSource> sources, bool hasAudio, RTCConfiguration configuration, string h264Parameters, string viewer, Action<string> signal)
    {
        _hasAudio = hasAudio;
        _viewer = viewer;
        _signal = signal;
        _connection = new RTCPeerConnection(configuration);

        int payloadType = FirstVideoPayloadType;
        foreach (StreamSource source in sources)
        {
            var track = new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, payloadType, VideoClockRate, h264Parameters), MediaStreamStatusEnum.SendOnly);
            _connection.addTrack(track);
            _videos[source.Id] = new VideoTrack(source, track, payloadType);
            _sourceByPayloadType[payloadType] = source.Id;
            payloadType = payloadType + 1 == TelephoneEventPayloadType ? payloadType + 2 : payloadType + 1;
        }

        if (hasAudio)
        {
            _connection.addTrack(new MediaStreamTrack(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU), MediaStreamStatusEnum.SendOnly));
        }

        _connection.onicecandidate += OnIceCandidate;
        _connection.onconnectionstatechange += OnConnectionStateChanged;
        _connection.OnReceiveReportByIndex += (_, _, media, report) => OnRtcpReport(media, report);
    }

    /// <summary>Raised once, when media can flow.</summary>
    public event Action<WebRtcPeer>? Connected;

    /// <summary>Raised once, when the connection failed (true) or was closed (false), including by <see cref="Close"/>.</summary>
    public event Action<WebRtcPeer, bool>? Ended;

    public bool IsConnected => _connected;

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

    /// <summary>
    /// Makes the offer and sends it, followed by the ICE candidates gathered meanwhile. If the
    /// offer cannot be made, closes the connection and throws.
    /// </summary>
    public void Offer()
    {
        RTCSessionDescriptionInit offer;
        try
        {
            offer = _connection.createOffer();
            _connection.setLocalDescription(offer).GetAwaiter().GetResult();
        }
        catch
        {
            Close("the offer could not be made");
            throw;
        }

        Log.Debug($"SDP offer for {_viewer}:\n{offer.sdp}");

        Dictionary<string, string> sourceByMid = [];
        foreach ((string mid, int payloadType) in VideoPayloadTypesByMid(offer.sdp))
        {
            if (_sourceByPayloadType.TryGetValue(payloadType, out string? sourceId))
            {
                sourceByMid[mid] = sourceId;
            }
        }

        // A browser rejects a candidate that arrives ahead of the offer it belongs to.
        lock (_earlyCandidates)
        {
            _signal(ViewerProtocol.Serialize(new OfferMessage(offer.sdp, sourceByMid)));
            _offerSent = true;
            _earlyCandidates.ForEach(_signal);
            _earlyCandidates.Clear();
        }
    }

    /// <summary>Uses the viewer's answer; says why not if it cannot be used.</summary>
    public SetDescriptionResultEnum Answer(RTCSessionDescriptionInit answer)
    {
        Log.Debug($"SDP {answer.type} from {_viewer}:\n{answer.sdp}");
        return _connection.setRemoteDescription(answer);
    }

    public void AddIceCandidate(RTCIceCandidateInit candidate) => _connection.addIceCandidate(candidate);

    /// <summary>Sends a frame of a stream; nothing goes out on a track until it has had a key frame.</summary>
    public void SendVideo(string sourceId, EncodedVideoFrame frame)
    {
        if (!_connected || !_videos.TryGetValue(sourceId, out VideoTrack? video) || video.Stream is not { } stream)
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

                // RTP time is the capture time, so it stays true to the capture clock across late
                // frames, dropped frames and pauses.
                uint timestamp = unchecked(video.TimestampBase + (uint)(frame.Timestamp * VideoClockRate / MediaClock.TicksPerSecond));
                foreach ((byte[] payload, bool marker) in H264Packetizer.Packetize(frame.Data))
                {
                    stream.SendRtpRaw(payload, timestamp, marker ? 1 : 0, video.PayloadType);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Video send to {_viewer} failed: {ex.Message}");
        }
    }

    /// <summary>Sends 20 ms of G.711 mu-law audio captured at <paramref name="captureTime"/>.</summary>
    public void SendAudio(byte[] muLaw, long captureTime)
    {
        if (!_connected || !_hasAudio)
        {
            return;
        }

        try
        {
            lock (_sendLock)
            {
                uint timestamp = unchecked(_audioTimestampBase + (uint)(captureTime * PcmFormat.SampleRate / MediaClock.TicksPerSecond));
                _connection.SendRtpRaw(SDPMediaTypesEnum.audio, muLaw, timestamp, 0, _audioPayloadType);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Audio send to {_viewer} failed: {ex.Message}");
        }
    }

    /// <summary>Holds a stream back until its next key frame: the viewer has not been receiving it, and has to start decoding afresh.</summary>
    public void WaitForKeyFrame(string sourceId)
    {
        lock (_sendLock)
        {
            if (_videos.TryGetValue(sourceId, out VideoTrack? video))
            {
                video.WaitingForKeyFrame = true;
            }
        }
    }

    /// <summary><see cref="WaitForKeyFrame"/> for every stream, after a pause.</summary>
    public void WaitForKeyFrames()
    {
        lock (_sendLock)
        {
            foreach (VideoTrack video in _videos.Values)
            {
                video.WaitingForKeyFrame = true;
            }
        }
    }

    public void Close(string reason)
    {
        try
        {
            _connection.Close(reason);
        }
        catch (Exception ex)
        {
            Log.Debug($"Closing the WebRTC connection to {_viewer}: {ex.Message}");
        }
    }

    private void OnIceCandidate(RTCIceCandidate? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        lock (_earlyCandidates)
        {
            if (_offerSent)
            {
                _signal(candidate.toJSON());
            }
            else
            {
                _earlyCandidates.Add(candidate.toJSON());
            }
        }
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState state)
    {
        Log.Debug($"Client {_viewer}: WebRTC {state}");

        if (state == RTCPeerConnectionState.connected && !_connected)
        {
            lock (_sendLock)
            {
                foreach (VideoTrack video in _videos.Values)
                {
                    video.Stream = _connection.VideoStreamList.Find(stream => ReferenceEquals(stream.LocalTrack, video.Track));
                    video.PayloadType = video.Stream?.GetSendingFormat().ID ?? video.PayloadType;
                    video.WaitingForKeyFrame = true;
                }

                _audioPayloadType = _hasAudio ? _connection.AudioStream?.GetSendingFormat().ID ?? 0 : 0;
            }

            _connected = true;
            Connected?.Invoke(this);
        }
        else if ((state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) && Interlocked.Exchange(ref _ended, 1) == 0)
        {
            Ended?.Invoke(this, state == RTCPeerConnectionState.failed);
        }
    }

    /// <summary>A viewer that lost packets asks for a new key frame with an RTCP PLI or FIR.</summary>
    private void OnRtcpReport(SDPMediaTypesEnum media, RTCPCompoundPacket report)
    {
        if (media != SDPMediaTypesEnum.video
            || report.Feedback is not { } feedback
            || feedback.Header.PacketType != RTCPReportTypesEnum.PSFB
            || feedback.Header.PayloadFeedbackMessageType is not (PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR))
        {
            return;
        }

        // The feedback names the stream by its SSRC; if it cannot be matched, refresh them all.
        VideoTrack[] videos = [.. _videos.Values];
        VideoTrack[] matched = [.. videos.Where(video => video.Track.Ssrc == feedback.MediaSSRC)];
        foreach (VideoTrack video in matched.Length > 0 ? matched : videos)
        {
            Log.Debug($"Client {_viewer} requested a key frame of {video.Source.Id}.");
            video.Source.RequestKeyFrame();
        }
    }

    /// <summary>One stream's video track.</summary>
    private sealed class VideoTrack(StreamSource source, MediaStreamTrack track, int payloadType)
    {
        public StreamSource Source { get; } = source;

        public MediaStreamTrack Track { get; } = track;

        /// <summary>Known once connected.</summary>
        public VideoStream? Stream { get; set; }

        /// <summary>The one offered, then the one the browser agreed to.</summary>
        public int PayloadType { get; set; } = payloadType;

        public uint TimestampBase { get; } = (uint)Random.Shared.Next();

        public bool WaitingForKeyFrame { get; set; } = true;
    }
}
