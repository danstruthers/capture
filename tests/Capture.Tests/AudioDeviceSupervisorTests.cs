using Capture.Audio;

namespace Capture.Tests;

/// <summary>
/// Which devices are open when, with stand-in devices: the selection, the default-device entries,
/// refusals, and devices that are slow, stop or will not open. AudioDeviceTests does the same with
/// this PC's real devices.
/// </summary>
public sealed class AudioDeviceSupervisorTests : IDisposable
{
    private readonly FakeAudioEndpoints _endpoints = new();
    private AudioDeviceSupervisor? _devices;

    public void Dispose()
    {
        _endpoints.OpenAll(); // a test that holds a device back must not hold up the shutdown
        _devices?.Dispose();
    }

    // The periodic check is off: each test calls CheckDevices itself, so that no timer runs a check in between.
    private AudioDeviceSupervisor Devices(params string[] selection) =>
        _devices = new AudioDeviceSupervisor(new MediaClock(), _endpoints, selection, checkInterval: Timeout.InfiniteTimeSpan);

    /// <summary>Devices open and close in the background; this waits for them.</summary>
    private void Settle() => Assert.True(_devices!.WaitUntilSettled(TimeSpan.FromSeconds(5)), "devices are still opening or closing");

    private static AudioEntryState[] States(AudioDeviceSupervisor devices) => [.. devices.GetEntries().Select(entry => entry.State)];

    [Fact]
    public void Selected_devices_are_open_only_while_active()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("line", AudioDeviceKind.Input);
        _endpoints.Add("speakers", AudioDeviceKind.Output, isDefault: true);
        AudioDeviceSupervisor devices = Devices("mic", "speakers");

        Assert.Empty(_endpoints.OpenIds);
        Assert.Equal([AudioEntryState.Idle, AudioEntryState.Idle], States(devices));

        devices.SetActive(true);
        Settle();
        Assert.Equal(["mic", "speakers"], _endpoints.OpenIds);
        Assert.Equal([AudioEntryState.Capturing, AudioEntryState.Capturing], States(devices));
        Assert.Equal(["mic", "speakers"], devices.Current.Sources.Select(source => source.Name).Order());

        devices.SetSelection(["speakers", "line"]);
        Settle();
        Assert.Equal(["line", "speakers"], _endpoints.OpenIds);
        Assert.Equal(1, _endpoints.TimesOpened("speakers")); // kept open, not opened again

        devices.SetActive(false);
        Settle();
        Assert.Empty(_endpoints.OpenIds);
        Assert.Empty(devices.Current.Sources);
        Assert.Equal(["speakers", "line"], devices.Selection);
    }

    [Fact]
    public void A_default_entry_shares_its_device_and_moves_when_the_windows_default_does()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("headset", AudioDeviceKind.Input);
        AudioDeviceSupervisor devices = Devices(AudioSelection.DefaultInput, "mic");

        devices.SetActive(true);
        Settle();
        Assert.Equal(["mic"], _endpoints.OpenIds);
        Assert.All(devices.GetEntries(), entry => Assert.Equal(("mic", AudioEntryState.Capturing), (entry.Device?.Id, entry.State)));

        _endpoints.SetDefault(AudioDeviceKind.Input, "headset");
        devices.CheckDevices();
        Settle();
        Assert.Equal(["headset", "mic"], _endpoints.OpenIds);
        Assert.Equal("headset", devices.GetEntries()[0].Device?.Id);
    }

    [Fact]
    public void A_device_that_cannot_be_recorded_is_refused_and_the_selection_stays_as_it_was()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("busy", AudioDeviceKind.Input).FailsToOpen = true;
        _endpoints.Add("unplugged", AudioDeviceKind.Output).Connected = false;
        AudioDeviceSupervisor devices = Devices("mic");
        devices.SetActive(true);
        Settle();

        Assert.Contains("busy cannot be recorded", Assert.Throws<RefusedException>(() => devices.SetSelection(["busy"])).Message);
        Assert.Contains("not connected", Assert.Throws<RefusedException>(() => devices.SetSelection(["mic", "unplugged"])).Message);
        Assert.Throws<RefusedException>(() => devices.SetSelection(["no-such-device"]));
        Assert.Contains("no playback device", Assert.Throws<RefusedException>(() => devices.SetSelection([AudioSelection.DefaultOutput])).Message);

        Settle();
        Assert.Equal(["mic"], devices.Selection);
        Assert.Equal(["mic"], _endpoints.OpenIds);
        Assert.Equal([AudioEntryState.Capturing], States(devices));
    }

    [Fact]
    public void A_slow_device_does_not_hold_up_the_others()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        _endpoints.Add("monitor", AudioDeviceKind.Output, isDefault: true).HeldBack = true;
        AudioDeviceSupervisor devices = Devices("mic", "monitor");

        var started = DateTime.UtcNow;
        devices.SetActive(true);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "turning audio on waited for the devices");

        Assert.True(SpinWait.SpinUntil(() => devices.GetEntries()[0].State == AudioEntryState.Capturing, TimeSpan.FromSeconds(5)));
        Assert.Equal(AudioEntryState.Starting, devices.GetEntries()[1].State);

        _endpoints.OpenAll();
        Settle();
        Assert.Equal([AudioEntryState.Capturing, AudioEntryState.Capturing], States(devices));
    }

    [Fact]
    public void A_device_that_will_not_open_is_tried_again_later_rather_than_at_every_check()
    {
        _endpoints.Add("busy", AudioDeviceKind.Input).FailsToOpen = true;
        AudioDeviceSupervisor devices = Devices("busy"); // accepted while idle: nothing is opened yet
        devices.SetActive(true);
        Settle();

        AudioEntryStatus status = Assert.Single(devices.GetEntries());
        Assert.Equal((AudioEntryState.Unavailable, "Another program is using it."), (status.State, status.Problem));

        devices.CheckDevices();
        Settle();
        Assert.Equal(1, _endpoints.TimesTried("busy"));

        // Turning audio off and on again gives it a fresh try.
        _endpoints.Device("busy").FailsToOpen = false;
        devices.SetActive(false);
        devices.SetActive(true);
        Settle();
        Assert.Equal([AudioEntryState.Capturing], States(devices));
        Assert.Equal(2, _endpoints.TimesTried("busy"));
    }

    [Fact]
    public void A_device_that_stops_is_shown_as_unavailable_and_opened_again_when_it_is_back()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true);
        AudioDeviceSupervisor devices = Devices("mic");
        devices.SetActive(true);
        Settle();

        _endpoints.Unplug("mic");
        devices.CheckDevices();
        Settle();
        AudioEntryStatus status = Assert.Single(devices.GetEntries());
        Assert.Equal((AudioEntryState.Unavailable, "not connected"), (status.State, status.Problem));
        Assert.Empty(_endpoints.OpenIds);

        _endpoints.Plug("mic");
        devices.CheckDevices();
        Settle();
        Assert.Equal([AudioEntryState.Capturing], States(devices));
        Assert.Equal(2, _endpoints.TimesOpened("mic"));
    }
}
