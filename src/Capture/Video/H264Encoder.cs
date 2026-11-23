using System.Diagnostics;
using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;
using MediaFoundation.Transform;

namespace Capture.Video;

internal enum EncoderPreference
{
    /// <summary>Hardware when a working hardware encoder exists, otherwise software.</summary>
    Auto,
    Hardware,
    Software,
}

internal enum H264Profile
{
    Baseline = 66,
    Main = 77,
    High = 100,
}

internal sealed record H264EncoderConfig(int Width, int Height, int Fps, int BitrateBps, int GopFrames, H264Profile Profile)
{
    public bool Bt709 => Height >= 720;

    public long FrameDuration => MediaClock.TicksPerSecond / Fps;
}

/// <summary>One encoded H.264 access unit in Annex B format.</summary>
internal sealed class EncodedVideoFrame
{
    /// <summary>The access unit. A key frame always carries its SPS and PPS ahead of the picture.</summary>
    public required byte[] Data { get; init; }

    public required long Timestamp { get; init; }

    public required long Duration { get; init; }

    public required bool IsKeyFrame { get; init; }

    /// <summary>On key frames: start code + SPS + start code + PPS. Null on other frames.</summary>
    public byte[]? SequenceHeader { get; init; }
}

/// <summary>
/// An H.264 encoder MFT configured for live, low-latency encoding of NV12 frames.
/// <para>
/// Hardware encoders are asynchronous MFTs: they ask for input and announce output through events,
/// which a dedicated thread pumps here. The Microsoft software encoder is synchronous and is driven
/// directly from the calling thread. Both deliver frames through <see cref="FrameEncoded"/>.
/// </para>
/// </summary>
internal sealed unsafe class H264Encoder : IDisposable
{
    /// <summary>Frames allowed to wait for an asynchronous encoder before the oldest is dropped.</summary>
    private const int MaxPendingInputs = 2;

    private static readonly TimeSpan SelfTestTimeout = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly H264EncoderConfig _config;
    private readonly IMFTransform _transform;
    private readonly IMFActivate? _activate;
    private readonly bool _ownsActivate;
    private readonly ICodecAPI? _codecApi;
    private readonly bool _isAsync;
    private readonly bool _transformAllocatesOutput;
    private readonly IMFMediaBuffer? _outputBuffer;
    private readonly Queue<IMFSample> _pendingInputs = new();
    private readonly ManualResetEventSlim _drainComplete = new(false);
    private readonly Thread? _eventThread;
    private readonly int _stallFrameLimit;

    private int _needInputCount;
    private int _framesSinceOutput;
    private long _framesSubmitted;
    private long _droppedInputs;
    private long _lastTimestamp = -1;
    private readonly ParameterSets _parameterSets = new();
    private bool _forceKeyFrameUnsupported;
    private volatile bool _keyFrameRequested;
    private volatile Exception? _failure;
    private volatile bool _disposed;

    /// <param name="activate">For a hardware encoder, the activation object it was created from; shut down when the encoder is.</param>
    /// <param name="ownsActivate">Whether the activation object is also released with the encoder.</param>
    internal H264Encoder(IMFTransform transform, IMFActivate? activate, string name, bool isHardware, H264EncoderConfig config, bool ownsActivate = false)
    {
        _transform = transform;
        _activate = activate;
        _ownsActivate = ownsActivate;
        _config = config;
        _stallFrameLimit = Math.Max(config.Fps * 3, 30);
        Name = name;
        IsHardware = isHardware;

        try
        {
            _isAsync = UnlockIfAsync();
            _codecApi = transform as ICodecAPI;

            // Encoders disagree on whether properties must be set before or after the media types,
            // so try before, then retry whatever was refused once the types are in place.
            (Guid Api, Variant Value, string Name)[] settings = BuildCodecSettings();
            bool[] applied = new bool[settings.Length];
            ApplyCodecSettings(settings, applied);
            SetMediaTypes();
            ApplyCodecSettings(settings, applied);

            for (int i = 0; i < settings.Length; i++)
            {
                if (!applied[i])
                {
                    Log.Debug($"{Name}: encoder property '{settings[i].Name}' was not accepted.");
                }
            }

            Mf.Check(_transform.GetOutputStreamInfo(0, out MFTOutputStreamInfo info), "Encoder GetOutputStreamInfo");
            _transformAllocatesOutput = (info.dwFlags & (MFTOutputStreamInfoFlags.ProvidesSamples | MFTOutputStreamInfoFlags.CanProvideSamples)) != 0;
            if (!_transformAllocatesOutput)
            {
                _outputBuffer = Mf.CreateBuffer(Math.Max(info.cbSize, config.Width * config.Height));
            }

            ReadSequenceHeaderFromOutputType();

            Mf.Check(_transform.ProcessMessage(MFTMessageType.NotifyBeginStreaming, IntPtr.Zero), "Encoder begin streaming");
            Mf.Check(_transform.ProcessMessage(MFTMessageType.NotifyStartOfStream, IntPtr.Zero), "Encoder start of stream");

            if (_isAsync)
            {
                _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "h264-encoder-events" };
                _eventThread.SetApartmentState(ApartmentState.MTA);
                _eventThread.Start();
            }
        }
        catch
        {
            _disposed = true;
            ReleaseTransform();
            throw;
        }
    }

    /// <summary>Raised for every encoded frame, in order, on the encoder's delivery thread.</summary>
    public event Action<EncodedVideoFrame>? FrameEncoded;

    public string Name { get; }

    public bool IsHardware { get; }

    public bool IsAsync => _isAsync;

    /// <summary>Input frames discarded because an asynchronous encoder was not ready for them.</summary>
    public long DroppedInputs => Interlocked.Read(ref _droppedInputs);

    /// <summary>Asks for the next encoded frame to be a key frame (IDR).</summary>
    public void RequestKeyFrame() => _keyFrameRequested = true;

    /// <summary>
    /// Submits one NV12 frame and takes ownership of the sample. Throws if the encoder has failed,
    /// so that the caller can replace it.
    /// </summary>
    public void Encode(IMFSample sample)
    {
        if (_failure is { } failure)
        {
            Mf.Release(sample);
            throw new InvalidOperationException($"{Name} failed: {failure.Message}", failure);
        }

        Interlocked.Increment(ref _framesSubmitted);
        if (_isAsync)
        {
            EncodeAsync(sample);
        }
        else
        {
            EncodeSync(sample);
        }
    }

    public void Dispose() => Close(drain: true);

    /// <param name="drain">Whether to let the encoder finish and deliver frames it is still holding.</param>
    public void Close(bool drain)
    {
        if (_disposed)
        {
            return;
        }

        // An encoder that was never given a frame holds nothing, and the Microsoft encoder takes
        // about a second to answer a drain in that state; a stream nobody watched would pay that on every shutdown.
        drain &= Interlocked.Read(ref _framesSubmitted) > 0;

        try
        {
            if (_isAsync)
            {
                lock (_lock)
                {
                    while (_pendingInputs.Count > 0)
                    {
                        Mf.Release(_pendingInputs.Dequeue());
                    }
                }

                if (drain && _failure is null && !Mf.Failed(_transform.ProcessMessage(MFTMessageType.CommandDrain, IntPtr.Zero)))
                {
                    _drainComplete.Wait(TimeSpan.FromSeconds(1));
                }

                _disposed = true;

                // Shutting the transform down is what makes the blocking GetEvent call return.
                (_transform as IMFShutdown)?.Shutdown();
                _eventThread?.Join(TimeSpan.FromSeconds(2));
            }
            else
            {
                List<EncodedVideoFrame>? frames = null;
                lock (_lock)
                {
                    if (drain && _failure is null)
                    {
                        _transform.ProcessMessage(MFTMessageType.NotifyEndOfStream, IntPtr.Zero);
                        if (!Mf.Failed(_transform.ProcessMessage(MFTMessageType.CommandDrain, IntPtr.Zero)))
                        {
                            try
                            {
                                DrainSyncLocked(ref frames);
                            }
                            catch (Exception ex)
                            {
                                Log.Debug($"{Name}: drain failed: {ex.Message}");
                            }
                        }
                    }

                    _disposed = true;
                }

                Deliver(frames);
                _transform.ProcessMessage(MFTMessageType.NotifyEndStreaming, IntPtr.Zero);
            }
        }
        finally
        {
            _disposed = true;
            ReleaseTransform();
        }
    }

    /// <summary>
    /// Feeds synthetic frames until the encoder produces a key frame with its SPS/PPS. This proves
    /// the whole path (type negotiation, events, output retrieval) works on the installed driver
    /// before the encoder is trusted with the live stream.
    /// </summary>
    internal void SelfTest()
    {
        using var keyFrameSeen = new ManualResetEventSlim(false);

        void OnFrame(EncodedVideoFrame frame)
        {
            if (frame.IsKeyFrame && frame.SequenceHeader is not null)
            {
                keyFrameSeen.Set();
            }
        }

        FrameEncoded += OnFrame;
        try
        {
            var elapsed = Stopwatch.StartNew();
            for (int i = 0; elapsed.Elapsed < SelfTestTimeout; i++)
            {
                Encode(CreateTestFrame(i));
                if (keyFrameSeen.Wait(50))
                {
                    return;
                }
            }

            throw new TimeoutException($"no key frame was produced within {SelfTestTimeout.TotalSeconds:0} s");
        }
        finally
        {
            FrameEncoded -= OnFrame;
        }
    }

    private bool UnlockIfAsync()
    {
        if (Mf.Failed(_transform.GetAttributes(out IMFAttributes attributes)) || attributes is null)
        {
            return false;
        }

        try
        {
            bool isAsync = !Mf.Failed(attributes.GetUINT32(MFAttributesClsid.MF_TRANSFORM_ASYNC, out int value)) && value != 0;
            if (isAsync)
            {
                Mf.Check(attributes.SetUINT32(MFAttributesClsid.MF_TRANSFORM_ASYNC_UNLOCK, 1), "Unlock asynchronous encoder");
            }

            attributes.SetUINT32(MFAttributesClsid.MF_LOW_LATENCY, 1);
            return isAsync;
        }
        finally
        {
            Mf.Release(attributes);
        }
    }

    private (Guid Api, Variant Value, string Name)[] BuildCodecSettings() =>
    [
        (CodecApiProperty.AVEncCommonRateControlMode, Variant.FromUInt32(CodecApiProperty.RateControlModeCbr), "rate control mode (CBR)"),
        (CodecApiProperty.AVEncCommonMeanBitRate, Variant.FromUInt32((uint)_config.BitrateBps), "mean bit rate"),
        (CodecApiProperty.AVLowLatencyMode, Variant.FromBool(true), "low latency mode"),
        (CodecApiProperty.AVEncMPVGOPSize, Variant.FromUInt32((uint)_config.GopFrames), "GOP size"),
        (CodecApiProperty.AVEncMPVDefaultBPictureCount, Variant.FromUInt32(0), "B-frame count"),
    ];

    private void ApplyCodecSettings((Guid Api, Variant Value, string Name)[] settings, bool[] applied)
    {
        if (_codecApi is null)
        {
            return;
        }

        for (int i = 0; i < settings.Length; i++)
        {
            if (!applied[i])
            {
                Guid api = settings[i].Api;
                Variant value = settings[i].Value;
                applied[i] = _codecApi.SetValue(ref api, ref value) >= 0;
            }
        }
    }

    private void SetMediaTypes()
    {
        string size = $"{_config.Width}x{_config.Height}@{_config.Fps}";

        // An encoder's output type has to be set first; its input types depend on it.
        IMFMediaType outputType = Mf.CreateVideoType(MFMediaType.H264, _config.Width, _config.Height, _config.Fps);
        try
        {
            Mf.Check(outputType.SetUINT32(MFAttributesClsid.MF_MT_AVG_BITRATE, _config.BitrateBps), "Set MF_MT_AVG_BITRATE");
            Mf.Check(outputType.SetUINT32(MFAttributesClsid.MF_MT_MPEG2_PROFILE, (int)_config.Profile), "Set MF_MT_MPEG2_PROFILE");
            SetType(outputType, isOutput: true, $"Encoder SetOutputType(H264 {size} {_config.Profile})");
        }
        finally
        {
            Mf.Release(outputType);
        }

        IMFMediaType inputType = Mf.CreateVideoType(MFMediaType.NV12, _config.Width, _config.Height, _config.Fps);
        try
        {
            SetType(inputType, isOutput: false, $"Encoder SetInputType(NV12 {size})");
        }
        finally
        {
            Mf.Release(inputType);
        }
    }

    private void SetType(IMFMediaType type, bool isOutput, string operation)
    {
        HResult Apply() => isOutput
            ? _transform.SetOutputType(0, type, MFTSetTypeFlags.None)
            : _transform.SetInputType(0, type, MFTSetTypeFlags.None);

        Mf.SetYuvColorInfo(type, _config.Bt709);
        HResult hr = Apply();
        if (Mf.Failed(hr))
        {
            // The colour description is optional; some encoders refuse types that carry it.
            type.DeleteItem(MFAttributesClsid.MF_MT_YUV_MATRIX);
            type.DeleteItem(MFAttributesClsid.MF_MT_VIDEO_PRIMARIES);
            type.DeleteItem(MFAttributesClsid.MF_MT_TRANSFER_FUNCTION);
            type.DeleteItem(MFAttributesClsid.MF_MT_VIDEO_NOMINAL_RANGE);
            hr = Apply();
        }

        Mf.Check(hr, operation);
    }

    private void ReadSequenceHeaderFromOutputType()
    {
        if (Mf.Failed(_transform.GetOutputCurrentType(0, out IMFMediaType type)) || type is null)
        {
            return;
        }

        try
        {
            if (Mf.GetBlob(type, MFAttributesClsid.MF_MT_MPEG_SEQUENCE_HEADER) is { } header)
            {
                _parameterSets.Remember(header);
            }
        }
        finally
        {
            Mf.Release(type);
        }
    }

    private void EncodeSync(IMFSample sample)
    {
        List<EncodedVideoFrame>? frames = null;
        lock (_lock)
        {
            try
            {
                ApplyKeyFrameRequestLocked();
                HResult hr = _transform.ProcessInput(0, sample, 0);
                if (hr == HResult.MF_E_NOTACCEPTING)
                {
                    DrainSyncLocked(ref frames);
                    hr = _transform.ProcessInput(0, sample, 0);
                }

                Mf.Check(hr, "Encoder ProcessInput");
                DrainSyncLocked(ref frames);
            }
            catch (Exception ex)
            {
                _failure = ex;
                throw;
            }
            finally
            {
                Mf.Release(sample);
            }
        }

        Deliver(frames);
    }

    private void DrainSyncLocked(ref List<EncodedVideoFrame>? frames)
    {
        while (true)
        {
            EncodedVideoFrame? frame = ProcessOutputLocked(out bool needMoreInput);
            if (frame is not null)
            {
                (frames ??= []).Add(frame);
            }

            if (needMoreInput)
            {
                return;
            }
        }
    }

    private void EncodeAsync(IMFSample sample)
    {
        lock (_lock)
        {
            if (Interlocked.Increment(ref _framesSinceOutput) > _stallFrameLimit)
            {
                Mf.Release(sample);
                _failure = new TimeoutException($"no output for {_stallFrameLimit} submitted frames");
                throw new InvalidOperationException($"{Name} stalled: {_failure.Message}", _failure);
            }

            while (_pendingInputs.Count >= MaxPendingInputs)
            {
                Mf.Release(_pendingInputs.Dequeue());
                Interlocked.Increment(ref _droppedInputs);
            }

            _pendingInputs.Enqueue(sample);
            FeedLocked();
        }
    }

    /// <summary>Hands queued frames to an asynchronous encoder, one per outstanding input request.</summary>
    private void FeedLocked()
    {
        while (_needInputCount > 0 && _pendingInputs.Count > 0 && !_disposed)
        {
            IMFSample sample = _pendingInputs.Dequeue();
            _needInputCount--;
            try
            {
                ApplyKeyFrameRequestLocked();
                Mf.Check(_transform.ProcessInput(0, sample, 0), "Encoder ProcessInput");
            }
            catch (Exception ex)
            {
                _failure ??= ex;
            }
            finally
            {
                Mf.Release(sample);
            }
        }
    }

    private void EventLoop()
    {
        try
        {
            var events = (IMFMediaEventGenerator)_transform;
            while (true)
            {
                HResult hr = events.GetEvent(MFEventFlag.None, out IMFMediaEvent mediaEvent);
                if (hr == HResult.MF_E_SHUTDOWN || _disposed)
                {
                    Mf.Release(mediaEvent);
                    return;
                }

                Mf.Check(hr, "Encoder GetEvent");

                MediaEventType type;
                HResult status;
                try
                {
                    Mf.Check(mediaEvent.GetType(out type), "IMFMediaEvent.GetType");
                    mediaEvent.GetStatus(out status);
                }
                finally
                {
                    Mf.Release(mediaEvent);
                }

                switch (type)
                {
                    case MediaEventType.METransformNeedInput:
                        lock (_lock)
                        {
                            _needInputCount++;
                            FeedLocked();
                        }

                        break;

                    case MediaEventType.METransformHaveOutput:
                        EncodedVideoFrame? frame;
                        lock (_lock)
                        {
                            frame = ProcessOutputLocked(out _);
                        }

                        if (frame is not null)
                        {
                            Deliver(frame);
                        }

                        break;

                    case MediaEventType.METransformDrainComplete:
                        _drainComplete.Set();
                        break;

                    case MediaEventType.MEError:
                        throw new MfException("Encoder error event", status);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _failure ??= ex;
            }
        }
    }

    private void ApplyKeyFrameRequestLocked()
    {
        if (!_keyFrameRequested)
        {
            return;
        }

        _keyFrameRequested = false;
        if (_codecApi is null || _forceKeyFrameUnsupported)
        {
            return;
        }

        Guid api = CodecApiProperty.AVEncVideoForceKeyFrame;
        Variant value = Variant.FromUInt32(1);
        int hr = _codecApi.SetValue(ref api, ref value);
        if (hr < 0)
        {
            _forceKeyFrameUnsupported = true;
            Log.Warn($"{Name} cannot produce key frames on request (0x{hr:X8}); new viewers and file segments wait for the next scheduled key frame.");
        }
    }

    /// <summary>Retrieves at most one output sample from the transform.</summary>
    private EncodedVideoFrame? ProcessOutputLocked(out bool needMoreInput)
    {
        needMoreInput = false;
        if (_disposed)
        {
            needMoreInput = true;
            return null;
        }

        IMFSample? ownSample = null;
        IMFSample? provided = null;
        try
        {
            if (!_transformAllocatesOutput)
            {
                // A fresh sample every time (so no attributes leak from one frame to the next) around one reused buffer.
                Mf.Check(_outputBuffer!.SetCurrentLength(0), "Reset encoder output buffer");
                ownSample = Mf.CreateSample(_outputBuffer);
            }

            HResult hr = Mf.ProcessOutput(_transform, ownSample, out provided);
            if (hr == HResult.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                needMoreInput = true;
                return null;
            }

            if (hr == HResult.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                RenegotiateOutputType();
                return null;
            }

            Mf.Check(hr, "Encoder ProcessOutput");
            IMFSample? output = ownSample ?? provided; // a transform that provides samples may have had none
            return output is null ? null : ReadFrame(output);
        }
        finally
        {
            Mf.Release(ownSample);
            Mf.Release(provided);
        }
    }

    private void RenegotiateOutputType()
    {
        for (int index = 0; ; index++)
        {
            Mf.Check(_transform.GetOutputAvailableType(0, index, out IMFMediaType type), "Encoder GetOutputAvailableType after stream change");
            try
            {
                if (!Mf.Failed(type.GetGUID(MFAttributesClsid.MF_MT_SUBTYPE, out Guid subtype)) && subtype == MFMediaType.H264)
                {
                    Mf.Check(_transform.SetOutputType(0, type, MFTSetTypeFlags.None), "Encoder SetOutputType after stream change");
                    ReadSequenceHeaderFromOutputType();
                    return;
                }
            }
            finally
            {
                Mf.Release(type);
            }
        }
    }

    private EncodedVideoFrame? ReadFrame(IMFSample sample)
    {
        if (Mf.Failed(sample.GetTotalLength(out int totalLength)) || totalLength == 0)
        {
            return null;
        }

        long timestamp = Mf.Failed(sample.GetSampleTime(out long sampleTime)) ? _lastTimestamp + _config.FrameDuration : sampleTime;
        long duration = Mf.Failed(sample.GetSampleDuration(out long sampleDuration)) || sampleDuration <= 0 ? _config.FrameDuration : sampleDuration;

        Mf.Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "IMFSample.ConvertToContiguousBuffer");
        try
        {
            Mf.Check(buffer.Lock(out IntPtr pointer, out _, out int length), "IMFMediaBuffer.Lock");
            try
            {
                // Parameter sets on their own are remembered, and there is nothing to show yet.
                if (_parameterSets.Complete(new ReadOnlySpan<byte>((void*)pointer, length)) is not { } unit)
                {
                    return null;
                }

                _lastTimestamp = timestamp;
                return new EncodedVideoFrame
                {
                    Data = unit.Data,
                    Timestamp = timestamp,
                    Duration = duration,
                    IsKeyFrame = unit.IsKeyFrame,
                    SequenceHeader = unit.IsKeyFrame ? _parameterSets.SequenceHeader : null,
                };
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Mf.Release(buffer);
        }
    }

    private void Deliver(List<EncodedVideoFrame>? frames)
    {
        if (frames is null)
        {
            return;
        }

        foreach (EncodedVideoFrame frame in frames)
        {
            Deliver(frame);
        }
    }

    private void Deliver(EncodedVideoFrame frame)
    {
        Interlocked.Exchange(ref _framesSinceOutput, 0);
        try
        {
            FrameEncoded?.Invoke(frame);
        }
        catch (Exception ex)
        {
            Log.Error($"Encoded frame handler failed: {ex.Message}");
        }
    }

    private IMFSample CreateTestFrame(int index)
    {
        int lumaSize = _config.Width * _config.Height;
        int size = lumaSize * 3 / 2;

        IMFMediaBuffer buffer = Mf.CreateBuffer(size);
        try
        {
            Mf.Check(buffer.Lock(out IntPtr pointer, out _, out _), "IMFMediaBuffer.Lock");
            var pixels = new Span<byte>((void*)pointer, size);
            pixels[..lumaSize].Fill((byte)(64 + index * 16 % 128)); // a different grey each frame
            pixels[lumaSize..].Fill(128);
            buffer.Unlock();
            Mf.Check(buffer.SetCurrentLength(size), "IMFMediaBuffer.SetCurrentLength");

            IMFSample sample = Mf.CreateSample(buffer);
            sample.SetSampleTime(index * _config.FrameDuration);
            sample.SetSampleDuration(_config.FrameDuration);
            return sample;
        }
        finally
        {
            Mf.Release(buffer);
        }
    }

    private void ReleaseTransform()
    {
        Mf.Release(_outputBuffer);
        Mf.Release(_transform);

        // For an activated (hardware) MFT this also lets the activation object create a fresh instance later.
        _activate?.ShutdownObject();
        if (_ownsActivate)
        {
            Mf.Release(_activate);
        }

        _drainComplete.Dispose();
    }
}
