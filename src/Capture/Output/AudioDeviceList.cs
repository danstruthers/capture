using Capture.Audio;

namespace Capture.Output;

/// <summary>
/// The audio device list viewers see and choose from (the state's audio.devices): what Windows
/// has connected, combined with what is selected and how each selected entry is doing.
/// </summary>
internal static class AudioDeviceList
{
    /// <summary>
    /// For each kind, recording devices first: the entry for Windows' default device, the connected
    /// devices by name, then any selected device that is not connected (so that it can be seen, and
    /// deselected).
    /// </summary>
    /// <param name="inputs">How each selected entry is doing, from <see cref="AudioMixer.GetInputs"/>.</param>
    /// <param name="connected">The connected devices, from <see cref="AudioDevices.Enumerate"/>.</param>
    public static IReadOnlyList<AudioDeviceEntry> Build(IReadOnlyList<AudioInputStatus> inputs, IReadOnlyList<AudioDeviceInfo> connected)
    {
        Dictionary<string, AudioInputStatus> statusOf = inputs.ToDictionary(input => input.Id, AudioDevices.IdComparer);

        // A device can be recorded without its own entry being selected: through the default-device entry.
        Dictionary<string, AudioInputStatus> capturing = inputs
            .Where(input => input.State == AudioInputState.Capturing)
            .DistinctBy(input => input.Device!.Id, AudioDevices.IdComparer)
            .ToDictionary(input => input.Device!.Id, AudioDevices.IdComparer);

        AudioDeviceEntry Entry(string id, AudioDeviceKind kind, string name, bool present, bool isDefault = false, string? device = null)
        {
            statusOf.TryGetValue(id, out AudioInputStatus? status);
            AudioInputStatus? live = status?.State == AudioInputState.Capturing ? status : capturing.GetValueOrDefault(id);
            AudioDeviceStatus shown = status?.State switch
            {
                AudioInputState.Idle => AudioDeviceStatus.Idle,
                AudioInputState.Starting => AudioDeviceStatus.Starting,
                AudioInputState.Unavailable => AudioDeviceStatus.Unavailable,
                _ => live is not null ? AudioDeviceStatus.Capturing : AudioDeviceStatus.Off,
            };

            return new AudioDeviceEntry(id, kind, name, device, isDefault, present, Selected: status is not null, shown, status?.Problem, live?.Format);
        }

        var entries = new List<AudioDeviceEntry>();
        foreach (AudioDeviceKind kind in new[] { AudioDeviceKind.Input, AudioDeviceKind.Output })
        {
            // While in use, the device the entry actually records; otherwise the default as last listed, which is fresher.
            string defaultEntry = AudioSelection.DefaultFor(kind);
            AudioDeviceInfo? current = statusOf.GetValueOrDefault(defaultEntry) is { State: not AudioInputState.Idle } recorded
                ? recorded.Device
                : connected.FirstOrDefault(device => device.Kind == kind && device.IsDefault);
            entries.Add(Entry(defaultEntry, kind, $"Default {AudioDevices.Describe(kind)}", present: current is not null, device: current?.Name));

            foreach (AudioDeviceInfo device in connected.Where(device => device.Kind == kind))
            {
                entries.Add(Entry(device.Id, kind, device.Name, present: true, isDefault: device.IsDefault));
            }

            foreach (AudioInputStatus input in inputs)
            {
                bool missing = !AudioSelection.IsDefault(input.Id) && !connected.Any(device => AudioDevices.IdComparer.Equals(device.Id, input.Id));
                if (missing && (input.Device?.Kind ?? AudioDeviceKind.Input) == kind)
                {
                    entries.Add(Entry(input.Id, kind, input.Device?.Name ?? input.Id, present: false));
                }
            }
        }

        return entries;
    }
}
