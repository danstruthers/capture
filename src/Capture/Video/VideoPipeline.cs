using System.Collections.Concurrent;
using System.Diagnostics;
using Capture.Interop;
using MediaFoundation;

namespace Capture.Video;

internal sealed record VideoSettings(
    int Fps,
    int? BitrateBps,
    double GopSeconds,
    H264Profile Profile,
    EncoderPreference Encoder,
    int? MaxWidth,
    int? MaxHeight,
    bool DrawCursor,
    CaptureMethod CaptureMethod,
    int JpegQuality)
{
    public const int MinFps = 1;
    public const int MaxFps = 60;
}

/// <summary>Running totals for the video path; subtract two snapshots to get rates.</summary>
internal readonly record struct VideoStats(
    long FramesCaptured,
    long FramesEncoded,
    long FramesDropped,
    long KeyFrames,
    long BytesEncoded,
    long JpegFrames,
    long JpegBytes,
    double CaptureMs,
    double ConvertMs,
    double EncodeMs,
    double JpegMs);

/// <summary>
/// One video stream: a capture area (a screen, or the whole desktop) → BGRX frame → H.264 and/or JPEG.
/// <para>
/// One thread captures at the frame rate and hands each frame to whichever encoders are wanted at
/// that moment; each encoder has its own thread, so a slow encode never delays the next screen
/// grab. An encoder that cannot keep up drops frames rather than queueing them, which keeps the
/// output live. When neither encoder is wanted the screen is not captured at all.
/// </para>
/// <para>
/// Threads and what they own:
/// <list type="bullet">
/// <item>The capture thread reads the screen and queues frames.</item>
/// <item>The H.264 thread converts and encodes. It alone replaces the encoder (on a frame rate
/// change or a hardware failure), so it alone writes <c>_encoder</c>, <c>_config</c> and
/// <c>_encoderPreference</c>; other threads only read the current values.</item>
/// <item>The JPEG thread encodes pictures.</item>
/// <item>Callers (the capture session) set the demand and the frame rate from any thread;
/// <c>_configLock</c> guards the frame rate change waiting for the H.264 thread.</item>
/// </list>
/// <see cref="FrameEncoded"/> is raised on whichever thread the encoder delivers on: the H.264
/// thread, a hardware encoder's event thread, or the thread disposing an encoder that is drained.
/// </para>
/// </summary>
internal sealed unsafe class VideoPipeline : IDisposable
{
    // Limits of the Microsoft software H.264 encoder, the encoder that must always be able to take over.
    private const int MaxEncodeWidth = 4096;
    private const int MaxEncodeHeight = 2304;

    private readonly VideoSettings _settings;
    private readonly MediaClock _clock;
    private readonly ScreenCapturer _capturer;
    private readonly VideoConverter _converter;
    private readonly BlockingCollection<(IMFSample Sample, long Timestamp)> _h264Queue = new(boundedCapacity: 2);
    private readonly ManualResetEventSlim _demand = new(false);
    private readonly AutoResetEvent _jpegSignal = new(false);
    private readonly object _configLock = new();
    private readonly Thread _captureThread;
    private readonly Thread _h264Thread;
    private readonly Thread _jpegThread;

    private volatile H264Encoder _encoder;
    private volatile H264EncoderConfig _config;
    private H264EncoderConfig? _pendingConfig;

    // Which kind of encoder to create when the frame rate changes. Starts as configured; becomes
    // Software once this stream has had to use the software encoder (no usable hardware encoder, or
    // one that failed while running), so that a change does not search for hardware encoders again.
    private EncoderPreference _encoderPreference;
    private PendingFrame? _jpegPending;
    private long _frameInterval;
    private volatile int _frameRate;
    private volatile bool _wantH264;
    private volatile bool _wantJpeg;
    private volatile bool _stopping;
    private volatile bool _disposed;
    private bool _started;

    private long _framesCaptured;
    private long _framesEncoded;
    private long _framesDropped;
    private long _keyFrames;
    private long _bytesEncoded;
    private long _jpegFrames;
    private long _jpegBytes;
    private long _captureTicks;
    private long _convertTicks;
    private long _encodeTicks;
    private long _jpegTicks;

    public VideoPipeline(CaptureArea area, VideoSettings settings, MediaClock clock)
    {
        _settings = settings;
        _clock = clock;
        Area = area;

        (int width, int height) = ChooseOutputSize(area, settings);
        _config = CreateConfig(width, height, settings.Fps);
        _frameInterval = _config.FrameDuration;
        _frameRate = settings.Fps;

        _capturer = new ScreenCapturer(area, settings.DrawCursor, settings.CaptureMethod);
        try
        {
            _converter = new VideoConverter(_capturer.Width, _capturer.Height, width, height, settings.Fps, MFMediaType.NV12, _config.Bt709);
            try
            {
                _encoder = H264Encoders.Create(_config, settings.Encoder);
                _encoder.FrameEncoded += OnFrameEncoded;
                _encoderPreference = _encoder.IsHardware ? settings.Encoder : EncoderPreference.Software;
            }
            catch
            {
                _converter.Dispose();
                throw;
            }
        }
        catch
        {
            _capturer.Dispose();
            throw;
        }

        _captureThread = NewThread(CaptureLoop, $"{area.Id}-capture");
        _h264Thread = NewThread(H264Loop, $"{area.Id}-h264");
        _jpegThread = NewThread(JpegLoop, $"{area.Id}-jpeg");
    }

    /// <summary>Raised for every H.264 frame, in order.</summary>
    public event Action<EncodedVideoFrame>? FrameEncoded;

    /// <summary>Raised for every JPEG picture, in order.</summary>
    public event Action<JpegFrame>? JpegEncoded;

    /// <summary>Raised once if the pipeline stops because of an unrecoverable error.</summary>
    public event Action<Exception>? Faulted;

    public CaptureArea Area { get; }

    /// <summary>Whether anything is using this stream, so that the screen is being captured.</summary>
    public bool IsCapturing => _wantH264 || _wantJpeg;

    /// <summary>The configuration of the H.264 stream being produced.</summary>
    public H264EncoderConfig Config => _config;

    /// <summary>The capture frame rate, including a change that has been asked for but not yet reached the encoder.</summary>
    public int FrameRate => _frameRate;

    public string CaptureMethod => _capturer.Method;

    public string EncoderName => _encoder.Name;

    public bool EncoderIsHardware => _encoder.IsHardware;

    public void Start()
    {
        _started = true;
        _h264Thread.Start();
        _jpegThread.Start();
        _captureThread.Start();
    }

    /// <summary>Says which outputs currently have someone using them. With neither, capture stops.</summary>
    public void SetDemand(bool h264, bool jpeg)
    {
        if (_disposed)
        {
            return; // a stream being replaced after a layout change
        }

        if (h264 && !_wantH264)
        {
            // Whoever just started consuming needs a picture it can decode from.
            _encoder.RequestKeyFrame();
        }

        _wantH264 = h264;
        _wantJpeg = jpeg;

        try
        {
            if (h264 || jpeg)
            {
                _demand.Set();
            }
            else
            {
                _demand.Reset();
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and here.
        }
    }

    /// <summary>
    /// Changes the frame rate while running. Capture follows immediately; the H.264 encoder is
    /// replaced by one configured for the new rate before the next frame it encodes.
    /// </summary>
    public void SetFrameRate(int fps)
    {
        lock (_configLock)
        {
            if (fps == _frameRate)
            {
                return;
            }

            H264EncoderConfig current = _config;
            _pendingConfig = CreateConfig(current.Width, current.Height, fps);
            _frameRate = fps;
            Volatile.Write(ref _frameInterval, _pendingConfig.FrameDuration);
        }
    }

    public void RequestKeyFrame() => _encoder.RequestKeyFrame();

    public VideoStats GetStats()
    {
        double toMs = 1000.0 / Stopwatch.Frequency;
        return new VideoStats(
            Interlocked.Read(ref _framesCaptured),
            Interlocked.Read(ref _framesEncoded),
            Interlocked.Read(ref _framesDropped) + (_disposed ? 0 : _encoder.DroppedInputs), // a retired encoder's count is already in
            Interlocked.Read(ref _keyFrames),
            Interlocked.Read(ref _bytesEncoded),
            Interlocked.Read(ref _jpegFrames),
            Interlocked.Read(ref _jpegBytes),
            Interlocked.Read(ref _captureTicks) * toMs,
            Interlocked.Read(ref _convertTicks) * toMs,
            Interlocked.Read(ref _encodeTicks) * toMs,
            Interlocked.Read(ref _jpegTicks) * toMs);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _demand.Set();
        _jpegSignal.Set();

        if (_started)
        {
            _captureThread.Join();
            _h264Queue.CompleteAdding();
            _h264Thread.Join();
            _jpegThread.Join();
        }

        while (_h264Queue.TryTake(out (IMFSample Sample, long Timestamp) item))
        {
            Mf.Release(item.Sample);
        }

        Mf.Release(Interlocked.Exchange(ref _jpegPending, null)?.Sample);

        RetireEncoder(drain: true);
        _converter.Dispose();
        _capturer.Dispose();
        _h264Queue.Dispose();
        _demand.Dispose();
        _jpegSignal.Dispose();
    }

    private static Thread NewThread(ThreadStart start, string name)
    {
        var thread = new Thread(start) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.MTA);
        return thread;
    }

    /// <summary>
    /// The video size: the area's own size, scaled down (never up, keeping the shape) to fit within
    /// the configured maximums and within what H.264 encoders accept.
    /// </summary>
    internal static (int Width, int Height) ChooseOutputSize(CaptureArea area, VideoSettings settings)
    {
        double scale = 1;
        if (settings.MaxWidth is { } maxWidth)
        {
            scale = Math.Min(scale, maxWidth / (double)area.Width);
        }

        if (settings.MaxHeight is { } maxHeight)
        {
            scale = Math.Min(scale, maxHeight / (double)area.Height);
        }

        double encoderLimit = Math.Min(MaxEncodeWidth / (double)area.Width, MaxEncodeHeight / (double)area.Height);
        if (encoderLimit < scale)
        {
            scale = encoderLimit;
            Log.Info($"{area.Name} is larger than H.264 encoders accept; its video is scaled to {ToEven(area.Width * scale)}x{ToEven(area.Height * scale)}.");
        }

        return (ToEven(area.Width * scale), ToEven(area.Height * scale));
    }

    /// <summary>4:2:0 video needs even dimensions.</summary>
    private static int ToEven(double value) => Math.Max(64, (int)Math.Round(value) & ~1);

    private H264EncoderConfig CreateConfig(int width, int height, int fps)
    {
        // Without an explicit bit rate: about 0.07 bits per pixel per frame, which keeps text sharp at
        // screen-content motion levels and scales the bit rate down with the frame rate.
        int bitrate = _settings.BitrateBps ?? (int)Math.Clamp((double)width * height * fps * 0.07, 500_000, 20_000_000);
        int gopFrames = Math.Max(1, (int)Math.Round(_settings.GopSeconds * fps));
        return new H264EncoderConfig(width, height, fps, bitrate, gopFrames, _settings.Profile);
    }

    private void CaptureLoop()
    {
        NativeMethods.timeBeginPeriod(1);
        try
        {
            long next = _clock.Now;
            bool desktopUnavailable = false;

            while (!_stopping)
            {
                bool wantH264 = _wantH264;
                bool wantJpeg = _wantJpeg;
                if (!wantH264 && !wantJpeg)
                {
                    // Nobody is recording or watching: leave the screen alone until someone is.
                    _demand.Wait();
                    next = _clock.Now;
                    continue;
                }

                long now = _clock.Now;
                long wait = next - now;
                if (wait > 0)
                {
                    if (wait > 20_000)
                    {
                        // Sleep in short steps so that a change of frame rate or demand is picked up promptly.
                        Thread.Sleep((int)Math.Min(wait / 10_000 - 1, 50));
                    }
                    else
                    {
                        Thread.Yield();
                    }

                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                bool captured = _capturer.Capture(out int error);
                if (captured != !desktopUnavailable)
                {
                    desktopUnavailable = !captured;
                    if (desktopUnavailable)
                    {
                        Log.Warn($"{Area.Name} cannot be captured right now ({ScreenCapturer.DescribeError(error)}); repeating the last frame until it can.");
                    }
                    else
                    {
                        Log.Info($"{Area.Name} is being captured again.");
                    }
                }

                long interval = Volatile.Read(ref _frameInterval);

                if (wantH264)
                {
                    IMFSample sample = CopyFrame(now, interval);
                    if (!_h264Queue.TryAdd((sample, now)))
                    {
                        Mf.Release(sample);
                        Interlocked.Increment(ref _framesDropped);
                    }
                }

                if (wantJpeg)
                {
                    // One slot, newest frame wins: JPEG viewers get the latest picture the encoder has time for.
                    var pending = new PendingFrame(CopyFrame(now, interval), now);
                    Mf.Release(Interlocked.Exchange(ref _jpegPending, pending)?.Sample);
                    _jpegSignal.Set();
                }

                Interlocked.Add(ref _captureTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref _framesCaptured);

                next += interval;
                if (now - next > interval || next - now > interval)
                {
                    // More than a frame off schedule (a stall, or the frame rate just changed): restart
                    // the schedule from here instead of bursting to catch up.
                    next = now + interval;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            NativeMethods.timeEndPeriod(1);
        }
    }

    /// <summary>A sample holding a copy of the frame just captured: the capturer reuses its buffer for the next one.</summary>
    private IMFSample CopyFrame(long timestamp, long duration) =>
        Mf.CreateSample(new ReadOnlySpan<byte>((void*)_capturer.Pixels, _capturer.SizeInBytes), timestamp, duration);

    private void H264Loop()
    {
        try
        {
            foreach ((IMFSample sample, long timestamp) in _h264Queue.GetConsumingEnumerable())
            {
                IMFSample converted;
                long started;
                try
                {
                    ApplyPendingConfig(); // not timed: setting up an encoder is not converting
                    started = Stopwatch.GetTimestamp();
                    converted = _converter.Convert(sample, timestamp, _config.FrameDuration);
                }
                finally
                {
                    Mf.Release(sample);
                }

                long convertedAt = Stopwatch.GetTimestamp();
                EncodeWithFallback(converted);

                Interlocked.Add(ref _convertTicks, convertedAt - started);
                Interlocked.Add(ref _encodeTicks, Stopwatch.GetTimestamp() - convertedAt);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// Switches to an encoder for a newly requested frame rate. An encoder's frame rate, rate
    /// control and GOP length are fixed when it is set up, so a change means a new encoder; its
    /// first frame is a key frame, and consumers carry on from there.
    /// </summary>
    private void ApplyPendingConfig()
    {
        H264EncoderConfig? pending;
        lock (_configLock)
        {
            pending = _pendingConfig;
            _pendingConfig = null;
        }

        if (pending is null)
        {
            return;
        }

        RetireEncoder(drain: true);
        H264Encoder replacement = H264Encoders.Create(pending, _encoderPreference);
        if (!replacement.IsHardware)
        {
            _encoderPreference = EncoderPreference.Software;
        }

        _config = pending;
        UseEncoder(replacement);
        Log.Debug($"{Area.Name}: {pending.Fps} fps, {pending.BitrateBps / 1000} kbit/s, {replacement.Name}.");
    }

    /// <summary>
    /// Shuts the current encoder down. With <paramref name="drain"/>, the frames it is still holding
    /// come out first and reach the recorder and the viewers like any other: that is why it is
    /// disposed while still subscribed, and unsubscribed only afterwards. (Unsubscribing first would
    /// silently cut the end off every recording at each frame rate change and at shutdown.)
    /// </summary>
    private void RetireEncoder(bool drain)
    {
        H264Encoder old = _encoder;
        old.Close(drain);
        old.FrameEncoded -= OnFrameEncoded;

        // Its count of dropped frames would otherwise disappear with it.
        Interlocked.Add(ref _framesDropped, old.DroppedInputs);
    }

    /// <summary>Makes <paramref name="encoder"/> the current encoder; called after <see cref="RetireEncoder"/>.</summary>
    private void UseEncoder(H264Encoder encoder)
    {
        encoder.FrameEncoded += OnFrameEncoded;
        _encoder = encoder;
    }

    /// <summary>
    /// Encodes a frame. If a hardware encoder fails while streaming (driver reset, device lost),
    /// the software encoder takes over; the stream continues from its first key frame.
    /// </summary>
    private void EncodeWithFallback(IMFSample sample)
    {
        try
        {
            _encoder.Encode(sample);
        }
        catch (Exception ex) when (_encoder.IsHardware && _settings.Encoder == EncoderPreference.Auto)
        {
            Log.Warn($"{Area.Name}: hardware encoder '{_encoder.Name}' failed ({ex.Message}); switching to the software encoder.");

            // A failed encoder has nothing worth draining.
            RetireEncoder(drain: false);
            _encoderPreference = EncoderPreference.Software;
            UseEncoder(H264Encoders.CreateSoftware(_config));
        }
    }

    private void JpegLoop()
    {
        JpegEncoder? encoder = null;
        try
        {
            while (!_stopping)
            {
                _jpegSignal.WaitOne();
                PendingFrame? pending = Interlocked.Exchange(ref _jpegPending, null);
                if (pending is null)
                {
                    continue;
                }

                try
                {
                    long started = Stopwatch.GetTimestamp();
                    H264EncoderConfig config = _config;
                    encoder ??= new JpegEncoder(_capturer.Width, _capturer.Height, config.Width, config.Height, config.Fps, _settings.JpegQuality);
                    JpegFrame frame = encoder.Encode(pending.Sample, pending.Timestamp);

                    Interlocked.Add(ref _jpegTicks, Stopwatch.GetTimestamp() - started);
                    Interlocked.Increment(ref _jpegFrames);
                    Interlocked.Add(ref _jpegBytes, frame.Data.Length);
                    JpegEncoded?.Invoke(frame);
                }
                finally
                {
                    Mf.Release(pending.Sample);
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private void OnFrameEncoded(EncodedVideoFrame frame)
    {
        Interlocked.Increment(ref _framesEncoded);
        Interlocked.Add(ref _bytesEncoded, frame.Data.Length);
        if (frame.IsKeyFrame)
        {
            Interlocked.Increment(ref _keyFrames);
        }

        FrameEncoded?.Invoke(frame);
    }

    private void Fail(Exception exception)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _demand.Set();
        _jpegSignal.Set();
        Faulted?.Invoke(exception);
    }

    private sealed record PendingFrame(IMFSample Sample, long Timestamp);
}
