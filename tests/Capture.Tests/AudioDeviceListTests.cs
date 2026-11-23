using Capture.Audio;
using Capture.Output;

namespace Capture.Tests;

/// <summary>The audio device list viewers choose from, built from what is connected and what is selected.</summary>
public sealed class AudioDeviceListTests
{
    private static readonly AudioDeviceInfo Microphone = new("mic", "Microphone", AudioDeviceKind.Input) { IsDefault = true };
    private static readonly AudioDeviceInfo LineIn = new("line", "Line in", AudioDeviceKind.Input);
    private static readonly AudioDeviceInfo Speakers = new("speakers", "Speakers", AudioDeviceKind.Output) { IsDefault = true };

    [Fact]
    public void Each_kind_starts_with_the_default_entry_then_the_connected_devices()
    {
        IReadOnlyList<AudioDeviceEntry> entries = AudioDeviceList.Build([], [Microphone, LineIn, Speakers]);

        Assert.Equal([AudioSelection.DefaultInput, "mic", "line", AudioSelection.DefaultOutput, "speakers"], entries.Select(entry => entry.Id));
        Assert.Equal("Microphone", entries[0].Device);
        Assert.True(entries[1].IsDefault);
        Assert.All(entries, entry => Assert.Equal((false, AudioDeviceStatus.Off), (entry.Selected, entry.Status)));
    }

    [Fact]
    public void A_device_recorded_through_the_default_entry_shows_as_live_without_being_selected()
    {
        IReadOnlyList<AudioDeviceEntry> entries = AudioDeviceList.Build(
            [new AudioEntryStatus(AudioSelection.DefaultInput, Microphone, AudioEntryState.Capturing, Format: "48000 Hz")],
            [Microphone, Speakers]);

        AudioDeviceEntry standIn = entries.Single(entry => entry.Id == AudioSelection.DefaultInput);
        AudioDeviceEntry microphone = entries.Single(entry => entry.Id == "mic");
        Assert.Equal((true, AudioDeviceStatus.Capturing), (standIn.Selected, standIn.Status));
        Assert.Equal((false, AudioDeviceStatus.Capturing, "48000 Hz"), (microphone.Selected, microphone.Status, microphone.Format));
    }

    [Fact]
    public void A_selected_device_that_is_not_connected_is_still_listed_so_that_it_can_be_deselected()
    {
        var headset = new AudioDeviceInfo("headset", "Headset", AudioDeviceKind.Output) { IsConnected = false };

        IReadOnlyList<AudioDeviceEntry> entries = AudioDeviceList.Build(
            [new AudioEntryStatus("headset", headset, AudioEntryState.Unavailable, "not connected")],
            [Microphone]);

        AudioDeviceEntry entry = entries.Single(entry => entry.Id == "headset");
        Assert.Equal((AudioDeviceKind.Output, false, true, AudioDeviceStatus.Unavailable, "not connected"), (entry.Kind, entry.Present, entry.Selected, entry.Status, entry.Problem));
        Assert.False(entries.Single(e => e.Id == AudioSelection.DefaultOutput).Present); // there is no playback device at all
    }
}
