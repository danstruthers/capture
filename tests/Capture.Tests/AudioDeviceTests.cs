using Capture.Audio;

namespace Capture.Tests;

/// <summary>
/// This PC's real audio devices: the list, looking devices up, and recording every one of them at
/// once through the mixer. Recording them is skipped on a machine without audio devices.
/// </summary>
[Collection(MediaFoundationCollection.Name)]
public sealed class AudioDeviceTests
{
    [Fact]
    public void Every_connected_device_is_listed_once_with_its_kind_and_the_defaults_marked()
    {
        IReadOnlyList<AudioDeviceInfo> devices = AudioDevices.Enumerate();

        Assert.Equal(devices.Count, devices.Select(device => device.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, devices.Count), devices.Select(device => device.Number));
        Assert.True(devices.SkipWhile(device => device.Kind == AudioDeviceKind.Input).All(device => device.Kind == AudioDeviceKind.Output), "recording devices come first");
        Assert.All(devices, device => Assert.True(device.IsConnected && device.Name.Length > 0, device.ToString()));

        foreach (AudioDeviceKind kind in new[] { AudioDeviceKind.Input, AudioDeviceKind.Output })
        {
            AudioDeviceInfo[] ofKind = [.. devices.Where(device => device.Kind == kind)];
            string? defaultId = AudioDevices.GetDefaultId(kind);
            if (ofKind.Length == 0)
            {
                Assert.Null(defaultId);
            }
            else
            {
                Assert.Equal(defaultId, Assert.Single(ofKind, device => device.IsDefault).Id);
            }
        }

        foreach (AudioDeviceInfo device in devices)
        {
            Assert.Equal(device with { IsDefault = false, Number = 0 }, AudioDevices.Get(device.Id));
        }

        Assert.Null(AudioDevices.Get("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public void A_device_is_found_by_its_number_or_its_name()
    {
        IReadOnlyList<AudioDeviceInfo> devices = AudioDevices.Enumerate();

        foreach (AudioDeviceInfo device in devices)
        {
            Assert.Equal(device, AudioDevices.Find(device.Number.ToString()));
            if (devices.Count(other => other.Name.Equals(device.Name, StringComparison.CurrentCultureIgnoreCase)) == 1)
            {
                Assert.Equal(device, AudioDevices.Find(device.Name.ToUpperInvariant())); // two devices of the same name need their numbers
            }
        }

        Assert.Contains("does not exist", Assert.Throws<ArgumentException>(() => AudioDevices.Find((devices.Count + 1).ToString())).Message);
        Assert.Contains("No audio device", Assert.Throws<ArgumentException>(() => AudioDevices.Find("no device is called this")).Message);
    }

    [FactNeeding(Needs.AudioDevices)]
    public void Every_device_can_be_recorded_at_once()
    {
        IReadOnlyList<AudioDeviceInfo> devices = AudioDevices.Enumerate();

        string[] selection = [.. devices.Select(device => device.Id), AudioSelection.DefaultInput, AudioSelection.DefaultOutput];
        selection = [.. selection.Where(entry => AudioSelection.KindOfDefault(entry) is not { } kind || devices.Any(device => device.Kind == kind))];

        var clock = new MediaClock();
        using var mixer = new AudioMixer(clock, new AudioDeviceSupervisor(clock, AudioDevices.System, selection));
        mixer.Start();
        mixer.SetActive(true);

        // They open in the background, side by side; some take seconds to wake up.
        Assert.True(mixer.Devices.WaitUntilSettled(TimeSpan.FromSeconds(20)), "the devices are still opening after 20 s");
        IReadOnlyList<AudioEntryStatus> inputs = mixer.Devices.GetEntries();
        Assert.Equal(selection.Length, inputs.Count);
        Assert.All(inputs, input => Assert.True(input.State == AudioEntryState.Capturing, $"{input.Device?.Name ?? input.Id}: {input.State} {input.Problem}"));
        Assert.All(inputs, input => Assert.Matches(@"^\d+ Hz, \d+ ch, \d+-bit (float|PCM)$", input.Format));

        // The default entries share their devices' sources: every device is in the mix once.
        Assert.Equal(devices.Count, inputs.Select(input => input.Device!.Id).Distinct().Count());

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (mixer.FramesProduced < 25 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.True(mixer.FramesProduced >= 25, $"only {mixer.FramesProduced} frames in 3 s");

        mixer.SetActive(false);
        Assert.All(mixer.Devices.GetEntries(), input => Assert.Equal(AudioEntryState.Idle, input.State));
        Assert.True(mixer.Devices.WaitUntilSettled(TimeSpan.FromSeconds(10)), "the devices are still closing after 10 s");
    }
}
