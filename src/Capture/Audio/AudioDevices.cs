using Capture.Interop;
using MediaFoundation;

namespace Capture.Audio;

internal sealed record AudioDeviceInfo(int Index, string Name, string EndpointId)
{
    /// <summary>The number shown to people: 1 for the first device.</summary>
    public int Number => Index + 1;
}

/// <summary>Audio capture (recording) endpoints as Media Foundation device sources.</summary>
internal static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> Enumerate()
    {
        var devices = new List<AudioDeviceInfo>();
        IMFAttributes attributes = CreateAudioCaptureAttributes();
        try
        {
            if (Mf.Failed(MFExtern.MFEnumDeviceSources(attributes, out IMFActivate[] activates, out int count)) || activates is null)
            {
                return devices;
            }

            for (int i = 0; i < count; i++)
            {
                string name = Mf.GetString(activates[i], MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? $"Audio device {i}";
                string id = Mf.GetString(activates[i], MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_ENDPOINT_ID) ?? string.Empty;
                devices.Add(new AudioDeviceInfo(i, name, id));
                Mf.Release(activates[i]);
            }
        }
        finally
        {
            Mf.Release(attributes);
        }

        return devices;
    }

    /// <summary>
    /// Opens a capture device. <paramref name="selector"/> is a device number from <see cref="Enumerate"/>
    /// or part of a device name; null opens the system default recording device.
    /// </summary>
    public static IMFMediaSource Open(string? selector, out string deviceName)
    {
        IMFAttributes attributes = CreateAudioCaptureAttributes();
        try
        {
            if (selector is null)
            {
                deviceName = "default recording device";
                Mf.Check(
                    attributes.SetUINT32(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_ROLE, (int)ERole.eConsole),
                    "Select default audio device");
            }
            else
            {
                AudioDeviceInfo device = Find(selector);
                deviceName = device.Name;
                Mf.Check(
                    attributes.SetString(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_ENDPOINT_ID, device.EndpointId),
                    "Select audio device");
            }

            Mf.Check(MFExtern.MFCreateDeviceSource(attributes, out IMFMediaSource source), $"Open audio device ({deviceName})");
            return source;
        }
        finally
        {
            Mf.Release(attributes);
        }
    }

    /// <summary>Resolves an index or name fragment to a device; throws <see cref="ArgumentException"/> if it matches none or several.</summary>
    public static AudioDeviceInfo Find(string selector)
    {
        IReadOnlyList<AudioDeviceInfo> devices = Enumerate();

        if (int.TryParse(selector, out int number))
        {
            return number >= 1 && number <= devices.Count
                ? devices[number - 1]
                : throw new ArgumentException($"Audio device {number} does not exist. Use --list-devices to see the available devices.");
        }

        List<AudioDeviceInfo> matches = devices.Where(d => d.Name.Contains(selector, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No audio recording device has a name containing \"{selector}\". Use --list-devices to see the available devices."),
            _ => throw new ArgumentException($"\"{selector}\" matches {matches.Count} audio devices ({string.Join("; ", matches.Select(m => m.Name))}). Use a longer name or the device number."),
        };
    }

    private static IMFAttributes CreateAudioCaptureAttributes()
    {
        IMFAttributes attributes = Mf.CreateAttributes(2);
        Mf.Check(
            attributes.SetGUID(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, CLSID.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID),
            "Set device source type");
        return attributes;
    }
}
