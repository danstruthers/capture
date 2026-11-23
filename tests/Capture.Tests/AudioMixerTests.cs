using System.Collections.Concurrent;
using Capture.Audio;

namespace Capture.Tests;

/// <summary>The mix itself, from stand-in devices that each record a constant level.</summary>
public sealed class AudioMixerTests : IDisposable
{
    private readonly FakeAudioEndpoints _endpoints = new();
    private readonly MediaClock _clock = new();
    private readonly BlockingCollection<AudioFrame> _frames = new();
    private AudioMixer? _mixer;

    public void Dispose()
    {
        _mixer?.Dispose();
        _frames.Dispose();
    }

    /// <summary>A mixer for the selection, running, with its frames going to <see cref="_frames"/>.</summary>
    private AudioMixer StartMixing(params string[] selection)
    {
        _mixer = new AudioMixer(_clock, new AudioDeviceSupervisor(_clock, _endpoints, selection, checkInterval: Timeout.InfiniteTimeSpan));
        _mixer.FrameMixed += _frames.Add;
        _mixer.Start();
        _mixer.SetActive(true);
        return _mixer;
    }

    [Fact]
    public void Each_selected_device_is_in_the_mix_once()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true).Level = 1000;
        _endpoints.Add("speakers", AudioDeviceKind.Output, isDefault: true).Level = -300;
        _endpoints.Add("headphones", AudioDeviceKind.Output).Level = 5000;

        // The microphone is selected twice, by its own entry and as the default; it is mixed in once.
        AudioMixer mixer = StartMixing(AudioSelection.DefaultInput, "mic", "speakers");
        Assert.True(mixer.Devices.WaitUntilSettled(TimeSpan.FromSeconds(5)), "devices are still opening");

        // Frames from before both devices were open may hold only one of them; take recent ones.
        while (_frames.TryTake(out _))
        {
        }

        for (int i = 0; i < 5; i++)
        {
            Assert.True(_frames.TryTake(out AudioFrame? frame, TimeSpan.FromSeconds(2)), "no audio frames");
            Assert.All(frame!.Samples, sample => Assert.Equal(700, sample));
        }
    }

    [Fact]
    public void Nothing_selected_is_silence()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true).Level = 1000;
        AudioMixer mixer = StartMixing();

        Assert.True(_frames.TryTake(out AudioFrame? frame, TimeSpan.FromSeconds(2)), "no audio frames");
        Assert.All(frame!.Samples, sample => Assert.Equal(0, sample));
        Assert.Empty(_endpoints.OpenIds);
        Assert.Equal("nothing (silence)", mixer.Devices.Description);
    }

    [Fact]
    public void Frames_follow_each_other_without_gaps_while_devices_come_and_go()
    {
        _endpoints.Add("mic", AudioDeviceKind.Input, isDefault: true).Level = 1000;
        _endpoints.Add("speakers", AudioDeviceKind.Output, isDefault: true).Level = 200;
        AudioMixer mixer = StartMixing("mic");
        Assert.True(mixer.Devices.WaitUntilSettled(TimeSpan.FromSeconds(5)), "devices are still opening");
        while (_frames.TryTake(out _))
        {
        }

        mixer.Devices.SetSelection(["speakers"]);
        mixer.Devices.SetSelection(["mic", "speakers"]);
        Assert.True(mixer.Devices.WaitUntilSettled(TimeSpan.FromSeconds(5)), "devices are still opening or closing");

        // Half a second of frames, from before the changes on.
        var frames = new List<AudioFrame>();
        while (frames.Count < 25)
        {
            Assert.True(_frames.TryTake(out AudioFrame? frame, TimeSpan.FromSeconds(2)), "no audio frames");
            frames.Add(frame!);
        }

        Assert.All(frames.Zip(frames.Skip(1)), pair => Assert.Equal(PcmFormat.FrameDuration, pair.Second.Timestamp - pair.First.Timestamp));

        // And now both are in the mix.
        while (_frames.TryTake(out _))
        {
        }

        Assert.True(_frames.TryTake(out AudioFrame? latest, TimeSpan.FromSeconds(2)), "no audio frames");
        Assert.All(latest!.Samples, sample => Assert.Equal(1200, sample));
    }
}
