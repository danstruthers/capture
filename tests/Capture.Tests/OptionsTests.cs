using System.Net;
using Capture.Audio;
using Capture.Output;
using Capture.Video;

namespace Capture.Tests;

public sealed class OptionsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("capture-options-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Config(string json)
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Loads with an empty configuration file unless the test supplies one, so the shipped capture.json plays no part.</summary>
    private Options Load(params string[] args) =>
        Options.Load(args.Contains("--config") ? args : ["--config", Config("{}"), .. args]);

    [Fact]
    public void Defaults()
    {
        Options options = Load();

        Assert.Equal(30, options.Fps);
        Assert.Equal(ScreenLayout.Separate, options.Layout);
        Assert.Empty(options.Screens);
        Assert.Null(options.MaxWidth);
        Assert.Equal(VideoMode.H264, options.VideoMode);
        Assert.Equal(AudioSourceKind.Microphone, options.AudioSource);
        Assert.Equal(EncoderPreference.Auto, options.Encoder);
        Assert.True(options.Record);
        Assert.True(options.Stream);
        Assert.True(options.RemoteControl);
        Assert.Equal(IPAddress.Loopback, options.Bind);
        Assert.Equal(8081, options.Port);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), options.OutputDirectory);
        Assert.Null(options.Output);
    }

    [Fact]
    public void The_output_folder_can_use_environment_variables_and_is_made_a_full_path()
    {
        string config = Config("""{ "outputDir": "%TEMP%\\recordings\\" }""");
        string temp = Environment.GetEnvironmentVariable("TEMP")!;

        Assert.Equal(Path.Combine(temp, "recordings"), Options.Load(["--config", config]).OutputDirectory);
        Assert.Equal(Path.GetFullPath("relative"), Load("--output-dir", "relative").OutputDirectory);
        Assert.Equal(Path.Combine(temp, "x.mp4"), Load("-o", @"%TEMP%\x.mp4").Output);
    }

    [Fact]
    public void An_output_folder_that_cannot_be_one_is_refused()
    {
        string file = Config("{}");

        Assert.Contains("is a file", Assert.Throws<ArgumentException>(() => Load("--output-dir", file)).Message);
        Assert.Contains("%CAPTURE_NOT_SET%", Assert.Throws<ArgumentException>(() => Load("--output-dir", @"%CAPTURE_NOT_SET%\videos")).Message);
        Assert.Contains("--output-dir", Assert.Throws<ArgumentException>(() => Load("--output-dir", " ")).Message);
    }

    [Fact]
    public void Command_line_values_are_parsed()
    {
        Options options = Load("--fps", "12", "--video-mode=jpeg", "--audio-source", "both", "--encoder", "software",
            "--bind", "0.0.0.0", "--allow-origin", "http://10.0.0.5:5174/", "--allow-origin", "*", "-o", "out.mp4", "--no-record",
            "--layout", "desktop", "--screens", "2, 1", "--screens", "3,2", "--max-width", "1920");

        Assert.Equal(12, options.Fps);
        Assert.Equal(ScreenLayout.Desktop, options.Layout);
        Assert.Equal([2, 1, 3], options.Screens);
        Assert.Equal(1920, options.MaxWidth);
        Assert.Equal(VideoMode.Jpeg, options.VideoMode);
        Assert.Equal(AudioSourceKind.Both, options.AudioSource);
        Assert.Equal(EncoderPreference.Software, options.Encoder);
        Assert.Equal(IPAddress.Any, options.Bind);
        Assert.Equal(["http://10.0.0.5:5174", "*"], options.AllowedOrigins);
        Assert.Equal("out.mp4", options.Output);
        Assert.False(options.Record);
    }

    [Fact]
    public void Configuration_file_supplies_defaults()
    {
        string config = Config("""
            // comments and trailing commas are fine
            {
              "fps": 15,
              "videoMode": "jpeg",
              "audio-source": "desktop",
              "record": false,
              "allowOrigin": ["http://a:1", "http://b:2"],
              "jpegQuality": 40,
              "layout": "desktop",
              "screens": [1, 3],
            }
            """);

        Options options = Options.Load(["--config", config]);

        Assert.Equal(15, options.Fps);
        Assert.Equal(VideoMode.Jpeg, options.VideoMode);
        Assert.Equal(AudioSourceKind.Desktop, options.AudioSource);
        Assert.False(options.Record);
        Assert.Equal(["http://a:1", "http://b:2"], options.AllowedOrigins);
        Assert.Equal(40, options.JpegQuality);
        Assert.Equal(ScreenLayout.Desktop, options.Layout);
        Assert.Equal([1, 3], options.Screens);
        Assert.Equal(config, options.ConfigFile);
    }

    [Fact]
    public void Audio_devices_can_be_given_several_times_and_as_a_list()
    {
        string config = Config("""{ "audioDevice": ["2", "Speakers (Realtek"] }""");

        Assert.Equal(["2", "Speakers (Realtek", "USB"], Options.Load(["--config", config, "--audio-device", " USB "]).AudioDeviceSelectors);
        Assert.Empty(Load().AudioDeviceSelectors);
    }

    [Fact]
    public void Command_line_overrides_the_configuration_file()
    {
        string config = Config("""{ "fps": 15, "record": false, "noAudio": true }""");

        Options options = Options.Load(["--config", config, "--fps", "50", "--record", "--audio"]);

        Assert.Equal(50, options.Fps);
        Assert.True(options.Record);
        Assert.True(options.Audio);
    }

    [Theory]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "61")]
    [InlineData("--fps", "fast")]
    [InlineData("--video-mode", "vp8")]
    [InlineData("--audio-source", "everything")]
    [InlineData("--audio-device", " ")]
    [InlineData("--bind", "not-an-address")]
    [InlineData("--jpeg-quality", "101")]
    [InlineData("--layout", "grid")]
    [InlineData("--screens", "0")]
    [InlineData("--screens", "1,two")]
    [InlineData("--max-width", "10")]
    public void Bad_values_are_refused_with_the_option_named(string option, string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Load(option, value));

        Assert.Contains(option, error.Message);
    }

    [Fact]
    public void Unknown_options_and_missing_values_are_refused()
    {
        Assert.Contains("--frames", Assert.Throws<ArgumentException>(() => Load("--frames", "3")).Message);
        Assert.Contains("needs a value", Assert.Throws<ArgumentException>(() => Load("--fps")).Message);
    }

    [Fact]
    public void A_mistake_in_the_configuration_file_names_the_file()
    {
        string config = Config("""{ "fps": 500 }""");

        ArgumentException error = Assert.Throws<ArgumentException>(() => Options.Load(["--config", config]));

        Assert.Contains(config, error.Message);
        Assert.Contains("--fps", error.Message);
    }

    [Fact]
    public void Settings_that_could_never_do_anything_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Load("--no-server", "--no-record"));
        Assert.Throws<ArgumentException>(() => Load("--no-record", "--no-stream", "--no-remote-control"));

        // Off at launch but startable from a viewer is fine.
        Options idle = Load("--no-record", "--no-stream");
        Assert.False(idle.Record);
        Assert.False(idle.Stream);
    }

    [Fact]
    public void Screens_all_undoes_an_earlier_list()
    {
        string config = Config("""{ "screens": "2" }""");

        Assert.Empty(Options.Load(["--config", config, "--screens", "all"]).Screens);
    }

    [Fact]
    public void A_configuration_file_that_does_not_exist_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => Options.Load(["--config", Path.Combine(_directory, "missing.json")]));
    }
}
