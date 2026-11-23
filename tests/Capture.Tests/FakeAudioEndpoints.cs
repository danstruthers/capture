using Capture.Audio;

namespace Capture.Tests;

internal sealed class FakeAudioDevice(string id, AudioDeviceKind kind)
{
    public string Id { get; } = id;

    public AudioDeviceKind Kind { get; } = kind;

    public bool Connected { get; set; } = true;

    public bool FailsToOpen { get; set; }

    /// <summary>Opening waits until <see cref="FakeAudioEndpoints.OpenAll"/>, like a device that is slow to wake up.</summary>
    public bool HeldBack { get; set; }

    /// <summary>The value of every sample it records.</summary>
    public short Level { get; set; }
}

/// <summary>Stand-in audio devices that record a constant level, can be unplugged, and keep count of how often they were opened.</summary>
internal sealed class FakeAudioEndpoints : IAudioEndpoints
{
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Dictionary<string, FakeAudioDevice> _devices = [];
    private readonly Dictionary<AudioDeviceKind, string> _defaults = [];
    private readonly List<FakeAudioSource> _sources = [];
    private readonly List<string> _attempts = [];

    /// <summary>The devices open right now, by ID in order.</summary>
    public string[] OpenIds
    {
        get
        {
            lock (_lock)
            {
                return [.. _sources.Where(source => !source.Disposed).Select(source => source.Device.Id).Order()];
            }
        }
    }

    public FakeAudioDevice Add(string id, AudioDeviceKind kind, bool isDefault = false)
    {
        lock (_lock)
        {
            var device = new FakeAudioDevice(id, kind);
            _devices[id] = device;
            if (isDefault)
            {
                _defaults[kind] = id;
            }

            return device;
        }
    }

    public FakeAudioDevice Device(string id)
    {
        lock (_lock)
        {
            return _devices[id];
        }
    }

    public void SetDefault(AudioDeviceKind kind, string id)
    {
        lock (_lock)
        {
            _defaults[kind] = id;
        }
    }

    /// <summary>Disconnects a device; its open sources stop as a real device's would.</summary>
    public void Unplug(string id)
    {
        lock (_lock)
        {
            _devices[id].Connected = false;
            foreach (FakeAudioSource source in _sources.Where(source => source.Device.Id == id))
            {
                source.Failure = new InvalidOperationException("The device was unplugged.");
            }
        }
    }

    public void Plug(string id)
    {
        lock (_lock)
        {
            _devices[id].Connected = true;
        }
    }

    /// <summary>Lets the devices that are held back finish opening.</summary>
    public void OpenAll() => _release.Set();

    public int TimesOpened(string id)
    {
        lock (_lock)
        {
            return _sources.Count(source => source.Device.Id == id);
        }
    }

    public int TimesTried(string id)
    {
        lock (_lock)
        {
            return _attempts.Count(attempt => attempt == id);
        }
    }

    public string? GetDefaultId(AudioDeviceKind kind)
    {
        lock (_lock)
        {
            return _defaults.GetValueOrDefault(kind);
        }
    }

    public AudioDeviceInfo? Get(string id)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(id, out FakeAudioDevice? device)
                ? new AudioDeviceInfo(id, id, device.Kind) { IsConnected = device.Connected }
                : null;
        }
    }

    public IAudioSource Open(AudioDeviceInfo device, MediaClock clock)
    {
        FakeAudioDevice fake;
        lock (_lock)
        {
            fake = _devices[device.Id];
            _attempts.Add(device.Id);
        }

        if (fake.HeldBack)
        {
            _release.Wait();
        }

        lock (_lock)
        {
            if (fake.FailsToOpen)
            {
                throw new InvalidOperationException("Another program is using it.");
            }

            var source = new FakeAudioSource(device, fake.Level, clock);
            _sources.Add(source);
            return source;
        }
    }
}

/// <summary>Has recorded three seconds of its level, from just before it was opened.</summary>
internal sealed class FakeAudioSource : IAudioSource
{
    public FakeAudioSource(AudioDeviceInfo device, short level, MediaClock clock)
    {
        Device = device;
        short[] samples = new short[PcmFormat.SampleRate * 3];
        Array.Fill(samples, level);
        Timeline.Write(clock.Now - MediaClock.FromSeconds(0.5), samples);
    }

    public AudioDeviceInfo Device { get; }

    public string Name => Device.Name;

    public string Format => "fake";

    public long Latency => 20 * 10_000;

    public AudioTimeline Timeline { get; } = new();

    public Exception? Failure { get; set; }

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}
