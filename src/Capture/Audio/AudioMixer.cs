using Capture.Interop;

namespace Capture.Audio;

internal enum AudioSourceKind
{
    None,
    Microphone,
    Desktop,
    Both,
}

/// <summary>
/// Produces the application's audio: a steady stream of 20 ms frames of 8 kHz mono 16-bit PCM,
/// mixed from the microphone, the desktop audio, both, or neither (silence).
/// <para>
/// The mixer runs off the session clock, a little behind real time. Each frame covers a fixed
/// window on that clock and is filled from whatever the selected sources recorded for that window,
/// so the sources stay aligned with each other and with the video, and the source can be switched
/// while running without a break in the output.
/// </para>
/// <para>
/// Devices are only open while the mixer is active, so nothing listens to the microphone when
/// nobody is recording or watching.
/// </para>
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    // How far behind real time a window is mixed: long enough for the slowest source's data to have
    // arrived. A Media Foundation recording device delivers ~50 ms buffers shortly after they end;
    // a loopback stream is polled every 10 ms.
    private const long MicrophoneLatency = 120 * 10_000;
    private const long DesktopLatency = 60 * 10_000;

    private readonly MediaClock _clock;
    private readonly string? _microphoneDevice;
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;

    private AudioSourceKind _kind;
    private bool _active;
    private MicrophoneSource? _microphone;
    private LoopbackSource? _desktop;
    private long _latency = DesktopLatency;
    private long _nextFrame = -1;
    private long _framesProduced;
    private volatile bool _stopping;
    private bool _started;
    private bool _disposed;

    public AudioMixer(MediaClock clock, string? microphoneDevice, AudioSourceKind source)
    {
        _clock = clock;
        _microphoneDevice = microphoneDevice;
        _kind = source;
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "audio-mixer" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Raised for every 20 ms frame, in order, on the mixer thread.</summary>
    public event Action<AudioFrame>? FrameCaptured;

    public AudioSourceKind Source
    {
        get
        {
            lock (_sync)
            {
                return _kind;
            }
        }
    }

    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    /// <summary>What is currently being recorded, for display.</summary>
    public string Description
    {
        get
        {
            lock (_sync)
            {
                string[] names = [.. new[] { _microphone?.Name, _desktop?.Name }.Where(n => n is not null).Cast<string>()];
                return names.Length > 0 ? string.Join(" + ", names) : _kind == AudioSourceKind.None ? "none (silence)" : $"{Describe(_kind)} (idle)";
            }
        }
    }

    public static string Describe(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Microphone => "microphone",
        AudioSourceKind.Desktop => "desktop audio",
        AudioSourceKind.Both => "microphone + desktop audio",
        _ => "none",
    };

    public void Start()
    {
        _started = true;
        _thread.Start();
    }

    /// <summary>
    /// Selects what to record. Throws <see cref="InvalidOperationException"/>, leaving the current
    /// selection in place, if a device that is needed right now cannot be opened.
    /// </summary>
    public void SetSource(AudioSourceKind kind)
    {
        lock (_sync)
        {
            if (kind == _kind)
            {
                return;
            }

            Apply(kind, _active, throwOnError: true);
        }
    }

    /// <summary>Opens the devices and starts producing frames, or stops and releases the devices.</summary>
    public void SetActive(bool active)
    {
        lock (_sync)
        {
            if (active == _active)
            {
                return;
            }

            Apply(_kind, active, throwOnError: false);
            _nextFrame = -1;
        }

        if (active)
        {
            _wake.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _wake.Set();
        if (_started)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        lock (_sync)
        {
            _microphone?.Dispose();
            _desktop?.Dispose();
            _microphone = null;
            _desktop = null;
        }

        _wake.Dispose();
    }

    /// <summary>Opens and closes devices so that exactly the ones the selection needs are open while active.</summary>
    private void Apply(AudioSourceKind kind, bool active, bool throwOnError)
    {
        bool wantMicrophone = active && kind is AudioSourceKind.Microphone or AudioSourceKind.Both;
        bool wantDesktop = active && kind is AudioSourceKind.Desktop or AudioSourceKind.Both;

        // Open what is missing first, so a failure leaves the running configuration untouched.
        MicrophoneSource? newMicrophone = null;
        LoopbackSource? newDesktop = null;
        try
        {
            if (wantMicrophone && _microphone is null)
            {
                newMicrophone = Open(() => new MicrophoneSource(_microphoneDevice, _clock), "The microphone", throwOnError);
            }

            if (wantDesktop && _desktop is null)
            {
                newDesktop = Open(() => new LoopbackSource(_clock), "Desktop audio", throwOnError);
            }
        }
        catch
        {
            newMicrophone?.Dispose();
            newDesktop?.Dispose();
            throw;
        }

        if (!wantMicrophone)
        {
            _microphone?.Dispose();
            _microphone = null;
        }

        if (!wantDesktop)
        {
            _desktop?.Dispose();
            _desktop = null;
        }

        _microphone ??= newMicrophone;
        _desktop ??= newDesktop;
        _kind = kind;
        _active = active;
        _latency = _microphone is not null ? MicrophoneLatency : DesktopLatency;
    }

    /// <summary>
    /// Opens a device. A failure is either thrown (when a person asked for this source and should be
    /// told) or logged (when capture is merely becoming active and should carry on with what there is).
    /// </summary>
    private static T? Open<T>(Func<T> open, string what, bool throwOnError) where T : class
    {
        try
        {
            return open();
        }
        catch (Exception ex)
        {
            string message = $"{what} cannot be captured: {ex.Message}";
            if (throwOnError)
            {
                throw new InvalidOperationException(message, ex);
            }

            Log.Warn($"{message} Continuing without it.");
            return null;
        }
    }

    private void MixLoop()
    {
        bool highResolutionTimer = false;
        try
        {
            Span<short> scratch = stackalloc short[PcmFormat.SamplesPerFrame];
            Span<int> sum = stackalloc int[PcmFormat.SamplesPerFrame];

            while (!_stopping)
            {
                bool active;
                lock (_sync)
                {
                    active = _active;
                }

                // Sleeping in 5 ms steps needs the 1 ms system timer; only ask for it while mixing.
                if (active != highResolutionTimer)
                {
                    _ = active ? NativeMethods.timeBeginPeriod(1) : NativeMethods.timeEndPeriod(1);
                    highResolutionTimer = active;
                }

                if (!active)
                {
                    _wake.Wait();
                    _wake.Reset();
                    continue;
                }

                while (TryMixNextFrame(scratch, sum) is { } frame)
                {
                    Interlocked.Increment(ref _framesProduced);
                    FrameCaptured?.Invoke(frame);
                }

                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Audio mixing stopped: {ex.Message}");
        }
        finally
        {
            if (highResolutionTimer)
            {
                NativeMethods.timeEndPeriod(1);
            }
        }
    }

    /// <summary>Mixes the next 20 ms window if it is far enough in the past to be complete.</summary>
    private AudioFrame? TryMixNextFrame(Span<short> scratch, Span<int> sum)
    {
        lock (_sync)
        {
            if (!_active)
            {
                return null;
            }

            DropFailedSources();

            long due = (_clock.Now - _latency) / PcmFormat.FrameDuration;
            if (_nextFrame < 0 || due - _nextFrame > 100)
            {
                // Just (re)started, or stalled for more than two seconds: begin at the present.
                _nextFrame = due;
            }

            if (_nextFrame >= due)
            {
                return null;
            }

            long firstSample = _nextFrame * PcmFormat.SamplesPerFrame;
            sum.Clear();

            foreach (IAudioSource? source in (ReadOnlySpan<IAudioSource?>)[_microphone, _desktop])
            {
                if (source is null)
                {
                    continue;
                }

                source.Timeline.Read(firstSample, scratch);
                for (int i = 0; i < sum.Length; i++)
                {
                    sum[i] += scratch[i];
                }
            }

            short[] samples = new short[PcmFormat.SamplesPerFrame];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)Math.Clamp(sum[i], short.MinValue, short.MaxValue);
            }

            var frame = new AudioFrame { Samples = samples, Timestamp = _nextFrame * PcmFormat.FrameDuration };
            _nextFrame++;
            return frame;
        }
    }

    private void DropFailedSources()
    {
        if (_microphone?.Failure is { } microphoneFailure)
        {
            Log.Warn($"The microphone stopped: {microphoneFailure.Message} Audio continues without it.");
            _microphone.Dispose();
            _microphone = null;
        }

        if (_desktop?.Failure is { } desktopFailure)
        {
            Log.Warn($"Desktop audio stopped: {desktopFailure.Message} Audio continues without it.");
            _desktop.Dispose();
            _desktop = null;
        }
    }
}
