using Capture.Interop;
using MediaFoundation;

namespace Capture.Audio;

internal enum AudioDeviceKind
{
    /// <summary>A recording device: a microphone, line in, ...</summary>
    Input,

    /// <summary>A playback device, recorded as what it plays (loopback).</summary>
    Output,
}

/// <summary>An audio device as Windows knows it.</summary>
/// <param name="Id">The endpoint ID, which stays the same for as long as the device is installed.</param>
internal sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind)
{
    /// <summary>Whether it is plugged in and enabled, so that it can be recorded.</summary>
    public bool IsConnected { get; init; } = true;

    /// <summary>
    /// Whether it was Windows' default device of its kind when <see cref="AudioDevices.Enumerate"/>
    /// listed it. Only that list says: a device looked up by ID (<see cref="AudioDevices.Get"/>) is never marked default.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>Its number in <see cref="AudioDevices.Enumerate"/> (and --list-devices), from 1; 0 for a device looked up by ID.</summary>
    public int Number { get; init; }
}

/// <summary>What the mixer needs from the audio devices. Tests put their own devices in its place.</summary>
internal interface IAudioEndpoints
{
    /// <summary>The ID of Windows' default device of a kind, or null when there is none.</summary>
    string? GetDefaultId(AudioDeviceKind kind);

    /// <summary>The device with this ID, connected or not; null when Windows does not know it.</summary>
    AudioDeviceInfo? Get(string id);

    /// <summary>Starts recording from a connected device. Throws if that cannot be done.</summary>
    IAudioSource Open(AudioDeviceInfo device, MediaClock clock);
}

/// <summary>
/// The audio devices of this PC, through Core Audio: the same devices, names and IDs as Windows'
/// sound settings. Recording devices are recorded through Media Foundation, playback devices by
/// WASAPI loopback.
/// </summary>
internal static class AudioDevices
{
    public static readonly IAudioEndpoints System = new SystemEndpoints();

    public static StringComparer IdComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>"recording device" or "playback device".</summary>
    public static string Describe(AudioDeviceKind kind) => kind == AudioDeviceKind.Input ? "recording device" : "playback device";

    /// <summary>The connected devices: recording devices, then playback devices, each kind by name, numbered from 1.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Enumerate()
    {
        List<AudioDeviceInfo> devices = WithEnumerator(enumerator =>
        {
            string? defaultInput = GetDefaultId(enumerator, AudioDeviceKind.Input);
            string? defaultOutput = GetDefaultId(enumerator, AudioDeviceKind.Output);
            var found = new List<AudioDeviceInfo>();

            Check(Wasapi.EnumAudioEndpoints(enumerator, Wasapi.DEVICE_STATE_ACTIVE, out IntPtr collection), "list the audio devices");
            try
            {
                Check(Wasapi.GetCount(collection, out uint count), "list the audio devices");
                for (uint i = 0; i < count; i++)
                {
                    if (Wasapi.GetItem(collection, i, out IntPtr device) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (Read(device) is { } info)
                        {
                            string? defaultId = info.Kind == AudioDeviceKind.Input ? defaultInput : defaultOutput;
                            found.Add(info with { IsDefault = IdComparer.Equals(info.Id, defaultId) });
                        }
                    }
                    finally
                    {
                        Wasapi.Release(ref device);
                    }
                }
            }
            finally
            {
                Wasapi.Release(ref collection);
            }

            return found;
        });

        return
        [
            .. devices
                .OrderBy(device => device.Kind)
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select((device, index) => device with { Number = index + 1 }),
        ];
    }

    /// <summary>The device with this ID, connected or not; null when Windows does not know it.</summary>
    public static AudioDeviceInfo? Get(string id) => WithEnumerator(enumerator =>
    {
        if (string.IsNullOrWhiteSpace(id) || Wasapi.GetDevice(enumerator, id, out IntPtr device) < 0)
        {
            return null;
        }

        try
        {
            return Read(device);
        }
        finally
        {
            Wasapi.Release(ref device);
        }
    });

    /// <summary>The ID of Windows' default device of a kind, or null when there is none.</summary>
    public static string? GetDefaultId(AudioDeviceKind kind) => WithEnumerator(enumerator => GetDefaultId(enumerator, kind));

    /// <summary>
    /// A connected device by its number in <see cref="Enumerate"/> or by its name or part of it.
    /// Throws <see cref="ArgumentException"/> if that matches no device or several.
    /// </summary>
    public static AudioDeviceInfo Find(string selector)
    {
        IReadOnlyList<AudioDeviceInfo> devices = Enumerate();
        string text = selector.Trim();

        if (int.TryParse(text, out int number))
        {
            return devices.FirstOrDefault(device => device.Number == number)
                ?? throw new ArgumentException($"Audio device {number} does not exist.");
        }

        if (devices.FirstOrDefault(device => device.Name.Equals(text, StringComparison.CurrentCultureIgnoreCase)) is { } exact)
        {
            return exact;
        }

        List<AudioDeviceInfo> matches = [.. devices.Where(device => device.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))];
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No audio device has a name containing \"{text}\"."),
            _ => throw new ArgumentException($"\"{text}\" matches {matches.Count} audio devices ({string.Join("; ", matches.Select(m => m.Name))}). Use more of the name, or the device number."),
        };
    }

    /// <summary>A Media Foundation source for a recording device.</summary>
    public static IMFMediaSource OpenInput(AudioDeviceInfo device)
    {
        IMFAttributes attributes = Mf.CreateAttributes(2);
        try
        {
            Mf.Check(
                attributes.SetGUID(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, CLSID.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID),
                "Set device source type");
            Mf.Check(
                attributes.SetString(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_ENDPOINT_ID, device.Id),
                "Select audio device");
            Mf.Check(MFExtern.MFCreateDeviceSource(attributes, out IMFMediaSource source), $"Open audio device ({device.Name})");
            return source;
        }
        finally
        {
            Mf.Release(attributes);
        }
    }

    private static string? GetDefaultId(IntPtr enumerator, AudioDeviceKind kind)
    {
        int dataFlow = kind == AudioDeviceKind.Input ? Wasapi.EDataFlowCapture : Wasapi.EDataFlowRender;
        if (Wasapi.GetDefaultAudioEndpoint(enumerator, dataFlow, out IntPtr device) < 0)
        {
            return null; // E_NOTFOUND: there are no devices of this kind
        }

        try
        {
            return Wasapi.GetId(device, out string id) >= 0 ? id : null;
        }
        finally
        {
            Wasapi.Release(ref device);
        }
    }

    private static AudioDeviceInfo? Read(IntPtr device)
    {
        if (Wasapi.GetId(device, out string id) < 0 || Wasapi.GetDataFlow(device, out int dataFlow) < 0)
        {
            return null;
        }

        bool connected = Wasapi.GetState(device, out uint state) >= 0 && state == Wasapi.DEVICE_STATE_ACTIVE;
        AudioDeviceKind kind = dataFlow == Wasapi.EDataFlowCapture ? AudioDeviceKind.Input : AudioDeviceKind.Output;
        return new AudioDeviceInfo(id, Wasapi.GetFriendlyName(device) ?? id, kind) { IsConnected = connected };
    }

    private static T WithEnumerator<T>(Func<IntPtr, T> use)
    {
        Check(Wasapi.CreateDeviceEnumerator(out IntPtr enumerator), "open the audio device list");
        try
        {
            return use(enumerator);
        }
        finally
        {
            Wasapi.Release(ref enumerator);
        }
    }

    private static void Check(int hr, string action)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"Could not {action} (0x{hr:X8}).");
        }
    }

    private sealed class SystemEndpoints : IAudioEndpoints
    {
        public string? GetDefaultId(AudioDeviceKind kind) => AudioDevices.GetDefaultId(kind);

        public AudioDeviceInfo? Get(string id) => AudioDevices.Get(id);

        public IAudioSource Open(AudioDeviceInfo device, MediaClock clock) =>
            device.Kind == AudioDeviceKind.Input ? new RecordingDeviceSource(device, clock) : new LoopbackSource(device, clock);
    }
}
