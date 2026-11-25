using System.Text.Json;
using Capture.Audio;
using Capture.Output;
using Capture.Video;

namespace Capture.Tests;

/// <summary>The messages between the capture client and its viewers: what is accepted, what is refused, and the JSON the viewer page reads.</summary>
public sealed class ViewerProtocolTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void A_configure_request_reads_every_setting_and_ignores_the_envelope()
    {
        ConfigureRequest request = ConfigureRequest.Parse(Json("""
            {"type":"configure","id":7,"frameRate":15,"videoMode":"jpeg","layout":"desktop","audioDevices":["default-input","{0.0.1}.{x}"]}
            """));

        Assert.Equal(15, request.FrameRate);
        Assert.Equal(VideoMode.Jpeg, request.VideoMode);
        Assert.Equal(ScreenLayout.Desktop, request.Layout);
        Assert.Equal(["default-input", "{0.0.1}.{x}"], request.AudioDevices);
        Assert.True(ConfigureRequest.Parse(Json("""{"type":"configure","id":8}""")).IsEmpty);
    }

    [Fact]
    public void The_audio_source_is_shorthand_for_the_default_devices()
    {
        Assert.Equal([AudioSelection.DefaultInput, AudioSelection.DefaultOutput], ConfigureRequest.Parse(Json("""{"audioSource":"both"}""")).AudioDevices);
        Assert.Empty(ConfigureRequest.Parse(Json("""{"audioSource":"none"}""")).AudioDevices!);
    }

    [Theory]
    [InlineData("""{"frameRate":0}""", "frame rate")]
    [InlineData("""{"frameRate":61}""", "frame rate")]
    [InlineData("""{"frameRate":"15"}""", "frame rate")]
    [InlineData("""{"frameRate":12.5}""", "frame rate")]
    [InlineData("""{"videoMode":"vp8"}""", "video mode")]
    [InlineData("""{"videoMode":1}""", "video mode")]
    [InlineData("""{"layout":"1"}""", "layout")]
    [InlineData("""{"layout":"separate,desktop"}""", "layout")]
    [InlineData("""{"audioSource":"everything"}""", "audio source")]
    [InlineData("""{"audioDevices":"default-input"}""", "audioDevices")]
    [InlineData("""{"audioDevices":[1]}""", "audioDevices")]
    [InlineData("""{"cameraFrameRate":0}""", "camera's frame rate")]
    [InlineData("""{"camera":""}""", "camera")]
    [InlineData("""{"camera":1}""", "camera")]
    [InlineData("""{"frameRate":15,"volume":3}""", "'volume'")]
    public void A_setting_that_is_not_valid_is_refused_saying_what_is_wrong(string json, string expected)
    {
        RefusedException error = Assert.Throws<RefusedException>(() => ConfigureRequest.Parse(Json(json)));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Commands_are_read_by_name()
    {
        Assert.Equal(
            new CommandRequest(CommandTarget.Recording, CommandAction.Start),
            CommandRequest.Parse(Json("""{"type":"command","target":"recording","action":"start","id":1}""")));
        Assert.Throws<RefusedException>(() => CommandRequest.Parse(Json("""{"type":"command","target":"recording","action":"rewind"}""")));
        Assert.Throws<RefusedException>(() => CommandRequest.Parse(Json("""{"type":"command","action":"start"}""")));
        Assert.Throws<RefusedException>(() => CommandRequest.Parse(Json("""{"type":"command","target":"0","action":"0"}""")));
    }

    [Fact]
    public void A_select_request_without_sources_means_every_stream()
    {
        Assert.Null(SelectRequest.Parse(Json("""{"type":"select"}""")).Sources);

        SelectRequest some = SelectRequest.Parse(Json("""{"type":"select","sources":["screen2"],"audio":false}"""));
        Assert.Equal(["screen2"], some.Sources!);
        Assert.False(some.Audio);
    }

    [Fact]
    public void The_state_has_the_names_and_values_the_viewer_reads()
    {
        var state = new StateMessage(
            RemoteControl: true,
            Layout: ScreenLayout.Separate,
            Recording: new RecordingInfo(RecordingState.Paused, ["a.mp4"], 1.5, 2048),
            Streaming: new StreamingInfo(StreamingState.Streaming, 1, 2),
            Video: new VideoInfo(VideoMode.H264, 30, 60),
            Sources: [new SourceInfo("screen1", 1, SourceKind.Screen, "Screen 1", [1], true, 0, 0, 2560, 1440, 2560, 1440, 30, 7741, true, 1, "GDI", "Microsoft H264 Encoder MFT", false)],
            Audio: new AudioInfo(true, "microphone", ["default-input"], "Microphone", 8000,
                [new AudioDeviceEntry("default-input", AudioDeviceKind.Input, "Default recording device", "Microphone", false, true, true, AudioDeviceStatus.Capturing, null, "48000 Hz, 1 ch")]),
            Camera: new CameraState(@"\\?\usb#cam", 30, "NV12 1920x1080 at 30 fps", null, [new CameraEntry(@"\\?\usb#cam", "USB Live Camera", true)]));

        JsonElement json = Json(ViewerProtocol.Serialize(state));

        Assert.Equal(["type", "remoteControl", "layout", "recording", "streaming", "video", "sources", "audio", "camera"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("state", json.GetProperty("type").GetString());
        Assert.Equal("separate", json.GetProperty("layout").GetString());
        Assert.Equal("paused", json.GetProperty("recording").GetProperty("state").GetString());
        Assert.Equal("streaming", json.GetProperty("streaming").GetProperty("state").GetString());
        Assert.Equal("h264", json.GetProperty("video").GetProperty("mode").GetString());
        Assert.Equal(
            ["bitrateKbps", "capture", "capturing", "encoder", "frameRate", "hardware", "height", "id", "kind", "left", "name", "number", "primary", "screens", "top", "videoHeight", "videoWidth", "viewers", "width"],
            json.GetProperty("sources")[0].EnumerateObject().Select(p => p.Name).Order());

        JsonElement audio = json.GetProperty("audio");
        Assert.Equal(["description", "devices", "enabled", "sampleRate", "selected", "source"], audio.EnumerateObject().Select(p => p.Name).Order());
        JsonElement device = audio.GetProperty("devices")[0];
        Assert.Equal(["default", "device", "format", "id", "kind", "name", "present", "problem", "selected", "status"], device.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("input", device.GetProperty("kind").GetString());
        Assert.Equal("capturing", device.GetProperty("status").GetString());
        Assert.Equal("screen", json.GetProperty("sources")[0].GetProperty("kind").GetString());

        JsonElement camera = json.GetProperty("camera");
        Assert.Equal(["devices", "frameRate", "mode", "problem", "selected"], camera.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["id", "name", "selected"], camera.GetProperty("devices")[0].EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public void A_configure_request_chooses_the_camera_and_its_frame_rate()
    {
        ConfigureRequest request = ConfigureRequest.Parse(Json("""{"camera":"\\\\?\\usb#cam","cameraFrameRate":15}"""));
        Assert.Equal(@"\\?\usb#cam", request.Camera);
        Assert.Equal(15, request.CameraFrameRate);
        Assert.False(request.IsEmpty);

        Assert.Equal(ConfigureRequest.NoCamera, ConfigureRequest.Parse(Json("""{"camera":"none"}""")).Camera);
        Assert.Equal(ConfigureRequest.NoCamera, ConfigureRequest.Parse(Json("""{"camera":false}""")).Camera);
        Assert.Equal(ConfigureRequest.NoCamera, ConfigureRequest.Parse(Json("""{"camera":null}""")).Camera);
    }

    [Fact]
    public void An_error_names_the_request_it_refuses()
    {
        JsonElement json = Json(ViewerProtocol.Error("No.", Json("""{"id":42}""").GetProperty("id")));

        Assert.Equal(("error", "No.", 42), (json.GetProperty("type").GetString(), json.GetProperty("message").GetString(), json.GetProperty("request").GetInt32()));
        Assert.Equal(JsonValueKind.Null, Json(ViewerProtocol.Error("No.")).GetProperty("request").ValueKind);
    }

    [Fact]
    public void A_media_message_starts_with_its_type_stream_number_and_timestamp()
    {
        byte[] message = ViewerProtocol.MediaMessage(ViewerProtocol.PictureMessage, 2, 0x0102030405060708, [0xAA, 0xBB]);

        Assert.Equal([1, 2, 8, 7, 6, 5, 4, 3, 2, 1, 0xAA, 0xBB], message);
    }
}
