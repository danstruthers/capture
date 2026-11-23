using Capture.Audio;

namespace Capture.Tests;

public sealed class AudioSelectionTests
{
    private static readonly AudioDeviceInfo Microphone = new("mic", "Microphone", AudioDeviceKind.Input);
    private static readonly AudioDeviceInfo Speakers = new("speakers", "Speakers", AudioDeviceKind.Output);

    [Theory]
    [InlineData("none", new string[0])]
    [InlineData("microphone", new[] { AudioSelection.DefaultInput })]
    [InlineData("desktop", new[] { AudioSelection.DefaultOutput })]
    [InlineData("both", new[] { AudioSelection.DefaultInput, AudioSelection.DefaultOutput })]
    public void The_audio_sources_are_windows_default_devices(string name, string[] selection)
    {
        AudioSourceKind source = Enum.Parse<AudioSourceKind>(name, ignoreCase: true);

        Assert.Equal(selection, AudioSelection.For(source));
        Assert.Equal(source, AudioSelection.AsSource(selection));
        Assert.Equal(source, AudioSelection.AsSource([.. selection.Reverse()]));
    }

    [Fact]
    public void A_selection_of_particular_devices_is_no_audio_source()
    {
        Assert.Null(AudioSelection.AsSource(["mic"]));
        Assert.Null(AudioSelection.AsSource([AudioSelection.DefaultInput, "speakers"]));
    }

    [Fact]
    public void A_device_from_the_command_line_takes_the_place_of_the_default_of_its_kind()
    {
        Assert.Equal(["mic"], AudioSelection.FromOptions(AudioSourceKind.Microphone, [Microphone]));
        Assert.Equal([AudioSelection.DefaultOutput, "mic"], AudioSelection.FromOptions(AudioSourceKind.Both, [Microphone]));
        Assert.Equal([AudioSelection.DefaultInput, "speakers"], AudioSelection.FromOptions(AudioSourceKind.Microphone, [Speakers]));
        Assert.Equal(["mic", "speakers"], AudioSelection.FromOptions(AudioSourceKind.Both, [Microphone, Speakers, Microphone]));
        Assert.Equal(["speakers"], AudioSelection.FromOptions(AudioSourceKind.None, [Speakers]));
        Assert.Equal([AudioSelection.DefaultInput], AudioSelection.FromOptions(AudioSourceKind.Microphone, []));
    }

    [Fact]
    public void Repeated_and_empty_entries_are_dropped()
    {
        Assert.Equal(["a", "b"], AudioSelection.Normalize(["a", " b ", "", "A", "b"]));
    }
}
