namespace Capture.Audio;

/// <summary>The audio sources of --audio-source: shorthands for Windows' default devices.</summary>
internal enum AudioSourceKind
{
    None,
    Microphone,
    Desktop,
    Both,
}

/// <summary>
/// What the audio is recorded from: a list of entries, each either an audio device's ID or one of
/// two stand-ins for Windows' default recording and playback device. A stand-in follows the
/// default when it changes; it and the device it stands for are recorded only once.
/// </summary>
internal static class AudioSelection
{
    public const string DefaultInput = "default-input";
    public const string DefaultOutput = "default-output";

    /// <summary>More than anyone would select; a limit on what a viewer can send.</summary>
    public const int MaxEntries = 64;

    public static bool IsDefault(string id) => id is DefaultInput or DefaultOutput;

    public static string DefaultFor(AudioDeviceKind kind) => kind == AudioDeviceKind.Input ? DefaultInput : DefaultOutput;

    public static AudioDeviceKind? KindOfDefault(string id) => id switch
    {
        DefaultInput => AudioDeviceKind.Input,
        DefaultOutput => AudioDeviceKind.Output,
        _ => null,
    };

    public static string[] For(AudioSourceKind source) => source switch
    {
        AudioSourceKind.Microphone => [DefaultInput],
        AudioSourceKind.Desktop => [DefaultOutput],
        AudioSourceKind.Both => [DefaultInput, DefaultOutput],
        _ => [],
    };

    /// <summary>The audio source a selection amounts to, or null when it names particular devices.</summary>
    public static AudioSourceKind? AsSource(IReadOnlyCollection<string> selection)
    {
        foreach (AudioSourceKind source in Enum.GetValues<AudioSourceKind>())
        {
            string[] entries = For(source);
            if (entries.Length == selection.Count && entries.All(selection.Contains))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>
    /// The selection the command line asks for: the default devices of <paramref name="source"/>
    /// and the given <paramref name="devices"/>. A recording device given takes the place of the
    /// default recording device, a playback device that of the default playback device.
    /// </summary>
    public static string[] FromOptions(AudioSourceKind source, IEnumerable<AudioDeviceInfo> devices)
    {
        List<AudioDeviceInfo> chosen = [.. devices.DistinctBy(device => device.Id, AudioDevices.IdComparer)];
        var selection = new List<string>(For(source).Where(entry => !chosen.Any(device => DefaultFor(device.Kind) == entry)));
        selection.AddRange(chosen.Select(device => device.Id));
        return [.. selection];
    }

    /// <summary>Trims the entries and drops empty and repeated ones, keeping the order.</summary>
    public static string[] Normalize(IEnumerable<string> entries) =>
        [.. entries.Select(entry => entry.Trim()).Where(entry => entry.Length > 0).Distinct(AudioDevices.IdComparer)];
}
