using System.Globalization;
using System.Net;
using System.Text.Json;
using Capture.Audio;
using Capture.Output;
using Capture.Video;

namespace Capture;

/// <summary>
/// Settings from the configuration file and the command line. Both use the same option names; the
/// file supplies defaults and the command line overrides them.
/// </summary>
internal sealed class Options
{
    public const string ConfigFileName = "capture.json";

    public const string Usage = """
        capture - records the desktop to MP4 and streams it to viewers.

        Video is H.264; audio is 16-bit linear PCM at 8 kHz mono. Viewers connect over a
        WebSocket, watch over WebRTC (or as JPEG pictures), and can control the capture.

        Usage: capture [options]

        Defaults come from capture.json (looked for in the current directory, then next to
        capture.exe); its keys are these option names without the dashes, e.g. "fps": 15,
        "videoMode": "jpeg", "record": false. The command line overrides the file.
        On/off options can be written --name or --no-name.

        Screens
          --layout <layout>      separate: each screen is its own stream (default)
                                 desktop: all screens in one stream, arranged as they
                                 are on the desktop. Viewers can switch it.
          --screens <list>       Which screens to capture, e.g. 1,3 (default: all).
                                 --list-devices shows the numbers.

        Video
          --fps <n>              Frames per second, 1-60 (default: 30). Viewers can
                                 change it while the capture is running.
          --video-mode <mode>    How viewers get video: h264 (WebRTC) or jpeg (pictures
                                 over the WebSocket, for when WebRTC or H.264 cannot be
                                 used). Default: h264. Viewers can switch it.
          --bitrate <kbps>       H.264 bit rate of each stream (default: from its size
                                 and the frame rate)
          --jpeg-quality <n>     JPEG quality, 1-100 (default: 60)
          --max-width <px>       Scale each stream down to at most this width
          --max-height <px>      ... and at most this height (the shape is kept)
          --gop <seconds>        Key frame interval (default: 2)
          --profile <name>       H.264 profile: baseline, main or high (default: baseline)
          --encoder <mode>       auto, hardware or software (default: auto - hardware
                                 when a working hardware encoder exists, else software)
          --capture <method>     auto, dxgi or gdi (default: auto - DXGI desktop
                                 duplication, with GDI as the fallback)
          --no-cursor            Leave the mouse pointer out of the video

        Audio
          --audio-source <src>   microphone, desktop (what the PC is playing), both or
                                 none (default: microphone): Windows' default recording
                                 and playback devices. Viewers can choose any devices.
          --audio-device <id>    Record this device: a number from --list-devices or part
                                 of its name. Repeat it for several. A recording device
                                 takes the place of the default microphone; a playback
                                 device (recorded as what it plays) that of the default
                                 desktop audio.
          --no-audio             No audio at all (no audio track, nothing to switch on)

        Recording
          --output-dir <folder>  Where recordings are saved (default: the TEMP folder).
                                 Environment variables work: %USERPROFILE%\Videos
          -o, --output <file>    File name (default: capture-<date>-<time>.mp4), in the
                                 output folder unless it is a full path. With several
                                 streams, one file each: name-screen1.mp4, ...
          --segment <seconds>    Start a new file every N seconds (name-001.mp4, ...)
          --overwrite            Replace the output file if it exists
          --no-record            Do not start recording at launch (a viewer can start it)

        Streaming and remote control
          --port <n>             WebSocket port (default: 8081)
          --bind <address>       Listen address (default: 127.0.0.1; use 0.0.0.0 to
                                 accept viewers from other machines)
          --allow-origin <url>   Extra web origin allowed to connect, e.g.
                                 http://192.168.1.20:5174 (repeatable; * allows any).
                                 Pages served from localhost are always allowed.
          --stun <url>           STUN server, e.g. stun:stun.l.google.com:19302
          --no-stream            Do not start streaming at launch (a viewer can start it)
          --no-remote-control    Viewers may watch but not control the capture
          --no-server            Do not listen at all: record only

        General
          --config <file>        Configuration file to use instead of capture.json
          --duration <seconds>   Stop after this long (default: run until Ctrl+C)
          --stats <seconds>      Status line interval; 0 turns it off (default: 5)
          --list-devices         List screens, audio devices and H.264 encoders
          -v, --verbose          Detailed logging
          -h, --help             Show this help
        """;

    private static readonly JsonDocumentOptions ConfigFormat = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The user's TEMP folder.</summary>
    public static string DefaultOutputDirectory => Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    public ScreenLayout Layout { get; private set; } = ScreenLayout.Separate;

    /// <summary>Screen numbers to capture; empty means all.</summary>
    public List<int> Screens { get; } = [];

    public int Fps { get; private set; } = 30;

    public VideoMode VideoMode { get; private set; } = VideoMode.H264;

    public int? BitrateKbps { get; private set; }

    public int JpegQuality { get; private set; } = 60;

    public int? MaxWidth { get; private set; }

    public int? MaxHeight { get; private set; }

    public double GopSeconds { get; private set; } = 2;

    public H264Profile Profile { get; private set; } = H264Profile.Baseline;

    public EncoderPreference Encoder { get; private set; } = EncoderPreference.Auto;

    public CaptureMethod Capture { get; private set; } = CaptureMethod.Auto;

    public bool DrawCursor { get; private set; } = true;

    public AudioSourceKind AudioSource { get; private set; } = AudioSourceKind.Microphone;

    /// <summary>Devices to record (--audio-device), as given: numbers or parts of names. Looked up when capture starts.</summary>
    public List<string> AudioDeviceSelectors { get; } = [];

    public bool Audio { get; private set; } = true;

    /// <summary>The folder recordings are saved in, as a full path.</summary>
    public string OutputDirectory { get; private set; } = DefaultOutputDirectory;

    /// <summary>The recording's file name or path; a relative one is relative to <see cref="OutputDirectory"/>.</summary>
    public string? Output { get; private set; }

    public double? SegmentSeconds { get; private set; }

    public bool Overwrite { get; private set; }

    public bool Record { get; private set; } = true;

    public int Port { get; private set; } = 8081;

    public IPAddress Bind { get; private set; } = IPAddress.Loopback;

    public List<string> AllowedOrigins { get; } = [];

    public string? Stun { get; private set; }

    public bool Stream { get; private set; } = true;

    public bool RemoteControl { get; private set; } = true;

    public bool Server { get; private set; } = true;

    public double? DurationSeconds { get; private set; }

    public double StatsSeconds { get; private set; } = 5;

    public bool ListDevices { get; private set; }

    public bool Verbose { get; private set; }

    public bool Help { get; private set; }

    /// <summary>The configuration file that supplied defaults, if one was found.</summary>
    public string? ConfigFile { get; private set; }

    /// <summary>
    /// Reads the configuration file, then the command line on top of it. Throws
    /// <see cref="ArgumentException"/> with a user-facing message on bad input.
    /// </summary>
    public static Options Load(string[] args)
    {
        string? configFile = FindConfigFile(args);
        var options = new Options { ConfigFile = configFile };

        if (configFile is not null)
        {
            try
            {
                options.Apply(ReadConfigFile(configFile));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException($"In {configFile}: {ex.Message}");
            }
        }

        options.Apply(args);
        options.Validate();
        return options;
    }

    /// <summary>Turns the properties of a JSON configuration object into the equivalent command-line arguments.</summary>
    internal static string[] ReadConfigFile(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), ConfigFormat);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("the file has to contain a JSON object.");
        }

        var arguments = new List<string>();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            string option = ToOptionName(property.Name);
            if (option is "config" or "help" or "list-devices")
            {
                throw new ArgumentException($"\"{property.Name}\" only makes sense on the command line.");
            }

            IEnumerable<JsonElement> values = property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray() : [property.Value];
            foreach (JsonElement value in values)
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.True:
                        arguments.Add($"--{option}");
                        break;
                    case JsonValueKind.False:
                        arguments.Add(option.StartsWith("no-", StringComparison.Ordinal) ? $"--{option[3..]}" : $"--no-{option}");
                        break;
                    case JsonValueKind.String:
                        arguments.Add($"--{option}");
                        arguments.Add(value.GetString()!);
                        break;
                    case JsonValueKind.Number:
                        arguments.Add($"--{option}");
                        arguments.Add(value.GetRawText());
                        break;
                    case JsonValueKind.Null:
                        break;
                    default:
                        throw new ArgumentException($"\"{property.Name}\" has to be a string, a number, true/false, or a list of those.");
                }
            }
        }

        return [.. arguments];
    }

    /// <summary>"videoMode" and "video-mode" both become "video-mode".</summary>
    private static string ToOptionName(string key)
    {
        var name = new System.Text.StringBuilder(key.Length + 4);
        foreach (char c in key)
        {
            if (char.IsUpper(c))
            {
                if (name.Length > 0 && name[^1] != '-')
                {
                    name.Append('-');
                }

                name.Append(char.ToLowerInvariant(c));
            }
            else
            {
                name.Append(c == '_' ? '-' : c);
            }
        }

        return name.ToString();
    }

    private static string? FindConfigFile(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string? explicitPath = args[i] == "--config" && i + 1 < args.Length ? args[i + 1]
                : args[i].StartsWith("--config=", StringComparison.Ordinal) ? args[i]["--config=".Length..]
                : null;

            if (explicitPath is not null)
            {
                return File.Exists(explicitPath)
                    ? Path.GetFullPath(explicitPath)
                    : throw new ArgumentException($"The configuration file {explicitPath} does not exist.");
            }
        }

        string[] candidates = [Path.GetFullPath(ConfigFileName), Path.Combine(AppContext.BaseDirectory, ConfigFileName)];
        return candidates.FirstOrDefault(File.Exists);
    }

    private void Apply(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            string? inlineValue = null;

            int equals = name.IndexOf('=');
            if (name.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                inlineValue = name[(equals + 1)..];
                name = name[..equals];
            }

            string Value()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }

                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{name} needs a value.");
                }

                return args[++i];
            }

            switch (name)
            {
                case "--layout": Layout = ParseEnum<ScreenLayout>(name, Value()); break;
                case "--screens": ParseScreens(name, Value()); break;
                case "--fps": Fps = ParseInt(name, Value(), 1, 60); break;
                case "--video-mode": VideoMode = ParseEnum<VideoMode>(name, Value()); break;
                case "--bitrate": BitrateKbps = ParseInt(name, Value(), 100, 100_000); break;
                case "--jpeg-quality": JpegQuality = ParseInt(name, Value(), 1, 100); break;
                case "--max-width": MaxWidth = ParseInt(name, Value(), 64, 8192); break;
                case "--max-height": MaxHeight = ParseInt(name, Value(), 64, 8192); break;
                case "--gop": GopSeconds = ParseDouble(name, Value(), 0.1, 60); break;
                case "--profile": Profile = ParseEnum<H264Profile>(name, Value()); break;
                case "--encoder": Encoder = ParseEnum<EncoderPreference>(name, Value()); break;
                case "--capture": Capture = ParseEnum<CaptureMethod>(name, Value()); break;
                case "--cursor": DrawCursor = true; break;
                case "--no-cursor": DrawCursor = false; break;
                case "--audio-source": AudioSource = ParseAudioSource(name, Value()); break;
                case "--audio-device": AudioDeviceSelectors.Add(ParseText(name, Value())); break;
                case "--audio": Audio = true; break;
                case "--no-audio": Audio = false; break;
                case "--output-dir": OutputDirectory = ParseFolder(name, Value()); break;
                case "-o" or "--output": Output = ExpandVariables(name, Value()); break;
                case "--segment": SegmentSeconds = ParseDouble(name, Value(), 1, 86_400); break;
                case "--overwrite": Overwrite = true; break;
                case "--no-overwrite": Overwrite = false; break;
                case "--record": Record = true; break;
                case "--no-record": Record = false; break;
                case "--port": Port = ParseInt(name, Value(), 1, 65535); break;
                case "--bind": Bind = ParseAddress(name, Value()); break;
                case "--allow-origin": AllowedOrigins.Add(Value().TrimEnd('/')); break;
                case "--stun": Stun = Value(); break;
                case "--stream": Stream = true; break;
                case "--no-stream": Stream = false; break;
                case "--remote-control": RemoteControl = true; break;
                case "--no-remote-control": RemoteControl = false; break;
                case "--server": Server = true; break;
                case "--no-server": Server = false; break;
                case "--config": Value(); break; // already used by FindConfigFile
                case "--duration": DurationSeconds = ParseDouble(name, Value(), 0.1, 31_536_000); break;
                case "--stats": StatsSeconds = ParseDouble(name, Value(), 0, 3600); break;
                case "--list-devices": ListDevices = true; break;
                case "-v" or "--verbose": Verbose = true; break;
                case "--no-verbose": Verbose = false; break;
                case "-h" or "--help" or "-?" or "/?": Help = true; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }
    }

    private void Validate()
    {
        if (Help || ListDevices)
        {
            return;
        }

        if (!Server && !Record)
        {
            throw new ArgumentException("--no-server and --no-record together leave nothing to do.");
        }

        if (!Record && (!RemoteControl || !Server) && !Stream)
        {
            throw new ArgumentException("With recording and streaming both off at launch and remote control disabled, nothing could ever start.");
        }
    }

    /// <summary>"1,3" or "all"; the option can be given more than once, and the numbers add up.</summary>
    private void ParseScreens(string name, string text)
    {
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            Screens.Clear();
            return;
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int number = ParseInt(name, part, 1, 64);
            if (!Screens.Contains(number))
            {
                Screens.Add(number);
            }
        }
    }

    /// <summary>A folder as a full path; a relative one is relative to the current directory. It does not have to exist yet.</summary>
    private static string ParseFolder(string name, string text)
    {
        string folder = ExpandVariables(name, text).Trim();
        if (folder.Length == 0)
        {
            throw new ArgumentException($"{name} needs a folder.");
        }

        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"{name} is not a usable folder (got '{text}'): {ex.Message}");
        }

        if (File.Exists(folder))
        {
            throw new ArgumentException($"{name} has to be a folder, but {folder} is a file.");
        }

        return folder;
    }

    /// <summary>
    /// Expands %NAME% environment variables, so that a configuration file can say "%USERPROFILE%\\Videos".
    /// A variable that is not set is an error rather than a folder literally called %NAME%.
    /// </summary>
    private static string ExpandVariables(string name, string text)
    {
        string expanded = Environment.ExpandEnvironmentVariables(text);
        System.Text.RegularExpressions.Match unknown = System.Text.RegularExpressions.Regex.Match(expanded, @"%([^%\\/]+)%");
        if (unknown.Success && Environment.GetEnvironmentVariable(unknown.Groups[1].Value) is null)
        {
            throw new ArgumentException($"{name} uses the environment variable {unknown.Value}, which is not set.");
        }

        return expanded;
    }

    private static string ParseText(string name, string text) =>
        string.IsNullOrWhiteSpace(text) ? throw new ArgumentException($"{name} needs a value.") : text.Trim();

    private static int ParseInt(string name, string text, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
        {
            throw new ArgumentException($"{name} must be a whole number from {min} to {max} (got '{text}').");
        }

        return value;
    }

    private static double ParseDouble(string name, string text, double min, double max)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < min || value > max)
        {
            throw new ArgumentException($"{name} must be a number from {min} to {max} (got '{text}').");
        }

        return value;
    }

    private static T ParseEnum<T>(string name, string text) where T : struct, Enum
    {
        if (!int.TryParse(text, out _) && Enum.TryParse(text, ignoreCase: true, out T value))
        {
            return value;
        }

        string choices = string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()));
        throw new ArgumentException($"{name} must be one of: {choices} (got '{text}').");
    }

    private static AudioSourceKind ParseAudioSource(string name, string text) => text.ToLowerInvariant() switch
    {
        "mic" => AudioSourceKind.Microphone,
        "system" or "loopback" => AudioSourceKind.Desktop,
        _ => ParseEnum<AudioSourceKind>(name, text),
    };

    private static IPAddress ParseAddress(string name, string text)
    {
        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(text, out IPAddress? address)
            ? address
            : throw new ArgumentException($"{name} must be an IP address such as 127.0.0.1 or 0.0.0.0 (got '{text}').");
    }
}
