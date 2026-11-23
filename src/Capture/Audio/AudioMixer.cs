using Capture.Interop;

namespace Capture.Audio;

/// <summary>
/// Produces the application's audio: a steady stream of 20 ms frames of 8 kHz mono 16-bit PCM,
/// mixed from the open audio devices (recording devices, and playback devices by loopback), or
/// silence when none is open.
/// <para>
/// The mixer runs off the session clock, a little behind real time. Each frame covers a fixed
/// window on that clock and is filled from whatever the devices recorded for that window, so the
/// devices stay aligned with each other and with the video, and devices can come and go while
/// running without a break in the output. Which devices are open is up to <see cref="Devices"/>;
/// the mixer takes whatever is open when it mixes a frame.
/// </para>
/// <para>
/// Threads: frames are mixed, and <see cref="FrameMixed"/> raised, on the mixer thread.
/// <see cref="SetActive"/> is called from the capture session's update thread.
/// </para>
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    private readonly MediaClock _clock;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;

    // Guards _active and _nextFrame, which the mixer thread works from and SetActive resets.
    private readonly object _lock = new();
    private bool _active;
    private long _nextFrame = -1;

    private long _framesProduced;
    private volatile bool _stopping;
    private bool _started;
    private bool _disposed;

    /// <param name="devices">The devices to mix from; the mixer owns them from here on.</param>
    public AudioMixer(MediaClock clock, AudioDeviceSupervisor devices)
    {
        _clock = clock;
        Devices = devices;
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "audio-mixer" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>Raised for every 20 ms frame, in order, on the mixer thread.</summary>
    public event Action<AudioFrame>? FrameMixed;

    /// <summary>What is recorded: the selection, and how each selected device is doing.</summary>
    public AudioDeviceSupervisor Devices { get; }

    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    public void Start()
    {
        _started = true;
        _thread.Start();
    }

    /// <summary>
    /// Starts mixing and opening the selected devices, or stops mixing and closes them. Returns at
    /// once: devices open in the background, and join the mix when they are ready.
    /// </summary>
    public void SetActive(bool active)
    {
        Devices.SetActive(active);
        lock (_lock)
        {
            if (active == _active)
            {
                return;
            }

            _active = active;
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

        Devices.Dispose();
        _wake.Dispose();
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
                lock (_lock)
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
                    FrameMixed?.Invoke(frame);
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
        OpenAudioSources open = Devices.Current;
        lock (_lock)
        {
            if (!_active)
            {
                return null;
            }

            // A window is complete once the slowest open device has delivered it.
            long due = (_clock.Now - open.Latency) / PcmFormat.FrameDuration;
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

            foreach (IAudioSource source in open.Sources)
            {
                if (source.Failure is not null)
                {
                    continue; // replaced at the next check
                }

                // A source closed since the snapshot was taken still reads: its timeline is only memory.
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
}
