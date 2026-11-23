using System.Collections.Concurrent;
using Capture.Audio;

namespace Capture.Tests;

/// <summary>
/// How the mixer handles the selection, with stand-in devices: which devices are open when, the
/// default-device entries, refusals, devices that are slow, stop or will not open, and the mix
/// itself. AudioDeviceTests does the same with this PC's real devices.
/// </summary>
public sealed class AudioMixerTests : IDisposable
{
    private readonly FakeEndpoints _endpoints = new();
    private readonly MediaClock _clock = new();
    private AudioMixer? _mixer;

    public void Dispose()
    {
        _endpoints.OpenAll(); // a test that holds a device back must not hold up the mixer's shutdown
        _mixer?.Dispose();
    }

    private AudioMixer Mixer(params string[] selection) => _mixer = new AudioMixer(_clock, _endpoints, selection);

    /// <summary>Devices open and close in the background; this waits for them.</summary>
    private void Settle() => Assert.True(_mixer!.WaitUntilSettled(TimeSpan.FromSeconds(5)), "devices are still opening or closing");

    private static AudioInputState[] States(AudioMixer mixer) => [.. mixer.GetInputs().Select(input => input.State)];

    [Fact]
    public void Selected_devices_are_open_only_while_the_mixer_is_active()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("line", AudioDeviceKind.Input);
        _endpoints.Add("speakers", AudioDeviceKind.Output, isDefault: true);
        AudioMixer mixer = Mixer("mic", "speakers");

        Assert.Empty(_endpoints.OpenIds);
        Assert.Equal([AudioInputState.Idle, AudioInputState.Idle], States(mixer));

        mixer.SetActive(true);
        Settle();
        Assert.Equal(["mic", "speakers"], _endpoints.OpenIds);
        Assert.Equal([AudioInputState.Capturing, AudioInputState.Capturing], States(mixer));

        mixer.SetSelection(["speakers", "line"]);
        Settle();
        Assert.Equal(["line", "speakers"], _endpoints.OpenIds);
        Assert.Equal(1, _endpoints.TimesOpened("speakers")); // kept open, not opened again

        mixer.SetActive(false);
        Settle();
        Assert.Empty(_endpoints.OpenIds);
        Assert.Equal(["speakers", "line"], mixer.Selection);
    }

    [Fact]
    public void A_default_entry_shares_its_device_and_moves_when_the_windows_default_does()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("headset", AudioDeviceKind.Input);
        AudioMixer mixer = Mixer(AudioSelection.DefaultInput, "mic");

        mixer.SetActive(true);
        Settle();
        Assert.Equal(["mic"], _endpoints.OpenIds);
        Assert.All(mixer.GetInputs(), input => Assert.Equal(("mic", AudioInputState.Capturing), (input.Device?.Id, input.State)));

        _endpoints.SetDefault(AudioDeviceKind.Input, "headset");
        mixer.CheckDevices();
        Settle();
        Assert.Equal(["headset", "mic"], _endpoints.OpenIds);
        Assert.Equal("headset", mixer.GetInputs()[0].Device?.Id);
    }

    [Fact]
    public void A_device_that_cannot_be_recorded_is_refused_and_the_selection_stays_as_it_was()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("busy", AudioDeviceKind.Input).FailsToOpen = true;
        _endpoints.Add("unplugged", AudioDeviceKind.Output).Connected = false;
        AudioMixer mixer = Mixer("mic");
        mixer.SetActive(true);
        Settle();

        Assert.Contains("busy cannot be recorded", Assert.Throws<InvalidOperationException>(() => mixer.SetSelection(["busy"])).Message);
        Assert.Contains("not connected", Assert.Throws<InvalidOperationException>(() => mixer.SetSelection(["mic", "unplugged"])).Message);
        Assert.Throws<InvalidOperationException>(() => mixer.SetSelection(["no-such-device"]));
        Assert.Contains("no playback device", Assert.Throws<InvalidOperationException>(() => mixer.SetSelection([AudioSelection.DefaultOutput])).Message);

        Settle();
        Assert.Equal(["mic"], mixer.Selection);
        Assert.Equal(["mic"], _endpoints.OpenIds);
        Assert.Equal([AudioInputState.Capturing], States(mixer));
    }

    [Fact]
    public void A_slow_device_does_not_hold_up_the_others()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("monitor", AudioDeviceKind.Output, isDefault: true).HeldBack = true;
        AudioMixer mixer = Mixer("mic", "monitor");

        var started = DateTime.UtcNow;
        mixer.SetActive(true);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "turning audio on waited for the devices");

        Assert.True(SpinWait.SpinUntil(() => mixer.GetInputs()[0].State == AudioInputState.Capturing, TimeSpan.FromSeconds(5)));
        Assert.Equal(AudioInputState.Starting, mixer.GetInputs()[1].State);

        _endpoints.OpenAll();
        Settle();
        Assert.Equal([AudioInputState.Capturing, AudioInputState.Capturing], States(mixer));
    }

    [Fact]
    public void A_device_that_will_not_open_is_tried_again_later_rather_than_at_every_check()
    {
        _endpoints.Add("busy", AudioDeviceKind.Input).FailsToOpen = true;
        AudioMixer mixer = Mixer("busy"); // accepted while idle: nothing is opened yet
        mixer.SetActive(true);
        Settle();

        AudioInputStatus status = Assert.Single(mixer.GetInputs());
        Assert.Equal((AudioInputState.Unavailable, "Another program is using it."), (status.State, status.Problem));

        mixer.CheckDevices();
        Settle();
        Assert.Equal(1, _endpoints.TimesTried("busy"));

        // Turning audio off and on again gives it a fresh try.
        _endpoints.Device("busy").FailsToOpen = false;
        mixer.SetActive(false);
        mixer.SetActive(true);
        Settle();
        Assert.Equal([AudioInputState.Capturing], States(mixer));
        Assert.Equal(2, _endpoints.TimesTried("busy"));
    }

    [Fact]
    public void A_device_that_stops_is_shown_as_unavailable_and_opened_again_when_it_is_back()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        AudioMixer mixer = Mixer("mic");
        mixer.SetActive(true);
        Settle();

        _endpoints.Unplug("mic");
        mixer.CheckDevices();
        Settle();
        AudioInputStatus status = Assert.Single(mixer.GetInputs());
        Assert.Equal((AudioInputState.Unavailable, "not connected"), (status.State, status.Problem));
        Assert.Empty(_endpoints.OpenIds);

        _endpoints.Plug("mic");
        mixer.CheckDevices();
        Settle();
        Assert.Equal([AudioInputState.Capturing], States(mixer));
        Assert.Equal(2, _endpoints.TimesOpened("mic"));
    }

    [Fact]
    public void Each_selected_device_is_in_the_mix_once()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true).Level = 1000;
        _endpoints.Add("speakers", AudioDeviceKind.Output, isDefault: true).Level = -300;
        _endpoints.Add("headphones", AudioDeviceKind.Output).Level = 5000;

        // The microphone is selected twice, by its own entry and as the default; it is mixed in once.
        AudioMixer mixer = Mixer(AudioSelection.DefaultInput, "mic", "speakers");
        using var frames = new BlockingCollection<AudioFrame>();
        mixer.FrameCaptured += frames.Add;
        mixer.Start();
        mixer.SetActive(true);
        Settle();

        // Frames from before both devices were open may hold only one of them; take recent ones.
        while (frames.TryTake(out _))
        {
        }

        for (int i = 0; i < 5; i++)
        {
            Assert.True(frames.TryTake(out AudioFrame? frame, TimeSpan.FromSeconds(2)), "no audio frames");
            Assert.All(frame!.Samples, sample => Assert.Equal(700, sample));
        }
    }

    [Fact]
    public void Nothing_selected_is_silence()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true).Level = 1000;
        AudioMixer mixer = Mixer();
        using var frames = new BlockingCollection<AudioFrame>();
        mixer.FrameCaptured += frames.Add;
        mixer.Start();
        mixer.SetActive(true);

        Assert.True(frames.TryTake(out AudioFrame? frame, TimeSpan.FromSeconds(2)), "no audio frames");
        Assert.All(frame!.Samples, sample => Assert.Equal(0, sample));
        Assert.Empty(_endpoints.OpenIds);
        Assert.Equal("nothing (silence)", mixer.Description);
    }

    private sealed class FakeDevice(string id, AudioDeviceKind kind)
    {
        public string Id { get; } = id;

        public AudioDeviceKind Kind { get; } = kind;

        public bool Connected { get; set; } = true;

        public bool FailsToOpen { get; set; }

        /// <summary>Opening waits until <see cref="FakeEndpoints.OpenAll"/>, like a device that is slow to wake up.</summary>
        public bool HeldBack { get; set; }

        /// <summary>The value of every sample it records.</summary>
        public short Level { get; set; }
    }

    /// <summary>Devices that record a constant level, can be unplugged, and keep count of how often they were opened.</summary>
    private sealed class FakeEndpoints : IAudioEndpoints
    {
        private readonly object _lock = new();
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Dictionary<string, FakeDevice> _devices = [];
        private readonly Dictionary<AudioDeviceKind, string> _defaults = [];
        private readonly List<FakeSource> _sources = [];
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

        public FakeDevice Add(string id, AudioDeviceKind kind, bool isDefault = false)
        {
            lock (_lock)
            {
                var device = new FakeDevice(id, kind);
                _devices[id] = device;
                if (isDefault)
                {
                    _defaults[kind] = id;
                }

                return device;
            }
        }

        public FakeDevice Device(string id)
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
                foreach (FakeSource source in _sources.Where(source => source.Device.Id == id))
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
                return _devices.TryGetValue(id, out FakeDevice? device)
                    ? new AudioDeviceInfo(id, id, device.Kind) { IsConnected = device.Connected }
                    : null;
            }
        }

        public IAudioSource Open(AudioDeviceInfo device, MediaClock clock)
        {
            FakeDevice fake;
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

                var source = new FakeSource(device, fake.Level, clock);
                _sources.Add(source);
                return source;
            }
        }
    }

    /// <summary>Has recorded three seconds of its level, from just before it was opened.</summary>
    private sealed class FakeSource : IAudioSource
    {
        public FakeSource(AudioDeviceInfo device, short level, MediaClock clock)
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
}
