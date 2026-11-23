using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Audio;
using Capture.Video;

namespace Capture.Output;

internal enum StreamingState
{
    Stopped,
    Streaming,
    Paused,
}

internal enum VideoMode
{
    /// <summary>H.264 video and G.711 audio over WebRTC.</summary>
    H264,

    /// <summary>JPEG pictures and raw PCM audio over the WebSocket itself.</summary>
    Jpeg,
}

/// <summary>
/// The messages between the capture client and its viewers: their JSON, the binary framing of
/// media on the WebSocket, and the checks on what viewers send. This file is the one definition
/// on this side; the README's "WebSocket protocol" section describes it for people, and
/// src/Capture.Viewer/wwwroot/viewer.js is the other side.
/// <para>
/// From the capture client: "state" (<see cref="StateMessage"/>, on connect, on every change and
/// once a second), "error" (<see cref="ErrorMessage"/>), "offer" (<see cref="OfferMessage"/>) and
/// ICE candidates. From a viewer: "select" (<see cref="SelectRequest"/>), "command"
/// (<see cref="CommandRequest"/>), "configure" (<see cref="ConfigureRequest"/>), and the WebRTC
/// answer and ICE candidates. JSON names are camelCase, and so are enum values ("h264").
/// </para>
/// </summary>
internal static class ViewerProtocol
{
    /// <summary>Binary message type: a JPEG picture of one stream.</summary>
    public const byte PictureMessage = 1;

    /// <summary>Binary message type: 20 ms of 16-bit little-endian PCM, 8 kHz mono.</summary>
    public const byte AudioMessage = 2;

    /// <summary>[type: 1 byte][stream number: 1 byte][timestamp: int64 little-endian, 100 ns units], then the payload.</summary>
    public const int MediaHeaderLength = 10;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Json);

    /// <param name="request">The "id" of the request this refuses, when the viewer gave one.</param>
    public static string Error(string message, JsonElement? request = null) => Serialize(new ErrorMessage(message, request));

    /// <summary>A binary media message: the header, then <paramref name="payload"/>.</summary>
    /// <param name="number">The stream number (screen number, 0 for the desktop); 0 for audio.</param>
    public static byte[] MediaMessage(byte type, int number, long timestamp, ReadOnlySpan<byte> payload)
    {
        byte[] message = new byte[MediaHeaderLength + payload.Length];
        message[0] = type;
        message[1] = (byte)number;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(2), timestamp);
        payload.CopyTo(message.AsSpan(MediaHeaderLength));
        return message;
    }

    /// <summary>The message's "type", or null when it has none (WebRTC answers and ICE candidates are recognised by their fields).</summary>
    public static string? TypeOf(JsonElement message) =>
        message.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    internal static string? GetString(JsonElement message, string name) =>
        message.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>One enum value given by its name ("desktop"): not a number, and not a combination ("separate,desktop").</summary>
    internal static T? ParseName<T>(string? text) where T : struct, Enum =>
        text is not null && !text.Contains(',') && !int.TryParse(text, out _) && Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value)
            ? value
            : null;
}

// ---------------------------------------------------------------------------------------------
// From the capture client
// ---------------------------------------------------------------------------------------------

/// <summary>What the capture client is doing and what it offers.</summary>
internal sealed record StateMessage(
    bool RemoteControl,
    ScreenLayout Layout,
    RecordingInfo Recording,
    StreamingInfo Streaming,
    VideoInfo Video,
    IReadOnlyList<SourceInfo> Sources,
    AudioInfo Audio)
{
    [JsonPropertyOrder(-1)]
    public string Type => "state";
}

/// <param name="Files">The file names (without folder) being written.</param>
/// <param name="Seconds">Recorded so far, pauses left out.</param>
internal sealed record RecordingInfo(RecordingState State, IReadOnlyList<string> Files, double Seconds, long Bytes);

/// <param name="Viewers">Clients being sent media right now.</param>
/// <param name="Clients">Clients connected.</param>
internal sealed record StreamingInfo(StreamingState State, int Viewers, int Clients);

internal sealed record VideoInfo(VideoMode Mode, int FrameRate, int JpegQuality);

/// <summary>One video stream: a screen, or the whole desktop.</summary>
/// <param name="Id">"screen1", "screen2", ... or "desktop".</param>
/// <param name="Number">Identifies the stream in binary messages: the screen number, or 0 for the desktop.</param>
/// <param name="Screens">The numbers of the screens it shows.</param>
/// <param name="Left">Position and size on the desktop, in pixels.</param>
/// <param name="VideoWidth">The encoded size (smaller when scaled down).</param>
/// <param name="Capturing">Whether anything uses it, so that it is being captured.</param>
/// <param name="Viewers">Clients receiving it.</param>
/// <param name="Capture">How the screen is read: "DXGI desktop duplication" or "GDI".</param>
/// <param name="Hardware">Whether the encoder is a hardware one.</param>
internal sealed record SourceInfo(
    string Id,
    int Number,
    string Name,
    IReadOnlyList<int> Screens,
    bool Primary,
    int Left,
    int Top,
    int Width,
    int Height,
    int VideoWidth,
    int VideoHeight,
    int BitrateKbps,
    bool Capturing,
    int Viewers,
    string Capture,
    string Encoder,
    bool Hardware);

/// <param name="Source">"microphone", "desktop", "both" or "none" when the selection is just Windows' default devices; otherwise "devices".</param>
/// <param name="Selected">The entries being recorded: device ids, "default-input", "default-output".</param>
/// <param name="Description">What is being recorded right now, for display.</param>
internal sealed record AudioInfo(bool Enabled, string Source, IReadOnlyList<string> Selected, string? Description, int SampleRate, IReadOnlyList<AudioDeviceEntry> Devices)
{
    public static AudioInfo Off { get; } = new(false, "none", [], null, PcmFormat.SampleRate, []);
}

internal enum AudioDeviceStatus
{
    /// <summary>Not selected, and not recorded through the default-device entry either.</summary>
    Off,

    /// <summary>Selected; nothing is being recorded or watched, so it is not open.</summary>
    Idle,

    /// <summary>Selected and being opened.</summary>
    Starting,

    /// <summary>Being recorded.</summary>
    Capturing,

    /// <summary>Selected but cannot be recorded; the entry's problem says why.</summary>
    Unavailable,
}

/// <summary>One choice in the audio device list.</summary>
/// <param name="Id">The Windows endpoint ID, or "default-input" / "default-output" for the entries that stand for Windows' default devices.</param>
/// <param name="Device">For the default-device entries: the name of the device they stand for now.</param>
/// <param name="IsDefault">Whether it is Windows' default device of its kind.</param>
/// <param name="Present">Whether it is connected (for a default-device entry: whether there is a default device).</param>
/// <param name="Format">What the device delivers, while it is recorded.</param>
internal sealed record AudioDeviceEntry(
    string Id,
    AudioDeviceKind Kind,
    string Name,
    string? Device,
    [property: JsonPropertyName("default")] bool IsDefault,
    bool Present,
    bool Selected,
    AudioDeviceStatus Status,
    string? Problem,
    string? Format);

/// <summary>A request was refused or failed.</summary>
/// <param name="Request">The "id" the viewer gave the request, if it gave one.</param>
internal sealed record ErrorMessage(string Message, JsonElement? Request)
{
    [JsonPropertyOrder(-1)]
    public string Type => "error";
}

/// <summary>A WebRTC offer (H.264 mode).</summary>
/// <param name="Sources">Which stream each video section carries, by the section's media id (mid).</param>
internal sealed record OfferMessage(string Sdp, IReadOnlyDictionary<string, string> Sources)
{
    [JsonPropertyOrder(-1)]
    public string Type => "offer";
}

// ---------------------------------------------------------------------------------------------
// From a viewer
// ---------------------------------------------------------------------------------------------

/// <summary>{"type":"select","sources":["screen1",...],"audio":true}: what one viewer wants to be sent.</summary>
/// <param name="Sources">Stream ids; null means every stream.</param>
internal sealed record SelectRequest(IReadOnlySet<string>? Sources, bool Audio)
{
    public static SelectRequest Parse(JsonElement message)
    {
        HashSet<string>? sources = message.TryGetProperty("sources", out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : null;
        bool audio = !message.TryGetProperty("audio", out JsonElement value) || value.ValueKind != JsonValueKind.False;
        return new SelectRequest(sources, audio);
    }
}

internal enum CommandTarget
{
    Recording,
    Streaming,
}

internal enum CommandAction
{
    Start,
    Stop,
    Pause,
    Resume,
}

/// <summary>{"type":"command","target":"recording","action":"start"}.</summary>
internal sealed record CommandRequest(CommandTarget Target, CommandAction Action)
{
    /// <summary>Throws <see cref="RefusedException"/> if the message is not a command this capture client knows.</summary>
    public static CommandRequest Parse(JsonElement message)
    {
        string? target = ViewerProtocol.GetString(message, "target");
        string? action = ViewerProtocol.GetString(message, "action");
        return ViewerProtocol.ParseName<CommandTarget>(target) is { } t && ViewerProtocol.ParseName<CommandAction>(action) is { } a
            ? new CommandRequest(t, a)
            : throw new RefusedException($"'{action} {target}' is not a command this capture client knows.");
    }

    public override string ToString() => $"{Action.ToString().ToLowerInvariant()} {Target.ToString().ToLowerInvariant()}";
}

/// <summary>
/// {"type":"configure", ...settings}: the settings to change; a setting that is null stays as it
/// is. "audioSource" (microphone, desktop, both, none) is shorthand for <see cref="AudioDevices"/>.
/// </summary>
internal sealed record ConfigureRequest
{
    public int? FrameRate { get; init; }

    public VideoMode? VideoMode { get; init; }

    public ScreenLayout? Layout { get; init; }

    /// <summary>Exactly the audio entries to record.</summary>
    public IReadOnlyList<string>? AudioDevices { get; init; }

    public bool IsEmpty => FrameRate is null && VideoMode is null && Layout is null && AudioDevices is null;

    /// <summary>
    /// Reads and checks every setting; throws <see cref="RefusedException"/>, saying what is wrong,
    /// if any is unknown or not valid. So a request with a mistake in it changes nothing.
    /// </summary>
    public static ConfigureRequest Parse(JsonElement message)
    {
        var request = new ConfigureRequest();
        foreach (JsonProperty setting in message.EnumerateObject())
        {
            JsonElement value = setting.Value;
            request = setting.Name switch
            {
                "type" or "id" => request, // the message's envelope, not settings
                "frameRate" => request with
                {
                    FrameRate = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int fps) && fps is >= VideoSettings.MinFps and <= VideoSettings.MaxFps
                        ? fps
                        : throw new RefusedException($"The frame rate has to be a whole number from {VideoSettings.MinFps} to {VideoSettings.MaxFps}."),
                },
                "videoMode" => request with
                {
                    VideoMode = ViewerProtocol.ParseName<VideoMode>(Text(value)) ?? throw new RefusedException("The video mode has to be h264 or jpeg."),
                },
                "layout" => request with
                {
                    Layout = ViewerProtocol.ParseName<ScreenLayout>(Text(value)) ?? throw new RefusedException("The layout has to be separate or desktop."),
                },
                "audioSource" => request with
                {
                    AudioDevices = AudioSelection.For(ViewerProtocol.ParseName<AudioSourceKind>(Text(value))
                        ?? throw new RefusedException("The audio source has to be microphone, desktop, both or none.")),
                },
                "audioDevices" => request with
                {
                    AudioDevices = value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(entry => entry.ValueKind == JsonValueKind.String)
                        ? [.. value.EnumerateArray().Select(entry => entry.GetString()!)]
                        : throw new RefusedException("audioDevices has to be a list of the ids in the state's audio.devices."),
                },
                _ => throw new RefusedException($"'{setting.Name}' is not a setting this capture client knows."),
            };
        }

        return request;
    }

    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
