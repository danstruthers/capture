using Capture.Audio;
using Capture.Interop;
using Capture.Output;
using Capture.Video;
using MediaFoundation;

namespace Capture.Tests;

/// <summary>
/// Runs synthetic screen frames and a test tone through the real converter, encoder and recorder,
/// then decodes the resulting MP4 files and checks what is in them.
/// </summary>
[Collection(MediaFoundationCollection.Name)]
public sealed class RecordingRoundTripTests : IDisposable
{
    private const double ToneFrequency = 440;
    private const double ToneAmplitude = 8000;

    private readonly string _directory = Directory.CreateTempSubdirectory("capture-recording-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_recording_plays_back_with_the_right_picture_sound_and_length()
    {
        string path = Path.Combine(_directory, "plain.mp4");
        using (var rig = new Rig(path))
        {
            rig.Recorder.Start();
            rig.Feed(frames: 45); // three seconds
        }

        // Nothing holds the file any more: it can be moved, deleted or opened by any program.
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        Mp4File file = Mp4File.Read(path);

        Assert.Equal((TestFrames.Width, TestFrames.Height), (file.Width, file.Height));
        Assert.Equal(45, file.VideoFrames);
        Assert.InRange(file.VideoSeconds, 2.9, 3.1);

        // Orientation and colour order: each quadrant has to come back as the colour it was drawn in.
        AssertColour(file, x: 160, y: 60, red: true, green: false, blue: false);   // top-left
        AssertColour(file, x: 480, y: 60, red: false, green: true, blue: false);   // top-right
        AssertColour(file, x: 160, y: 300, red: false, green: false, blue: true);  // bottom-left
        AssertColour(file, x: 480, y: 300, red: true, green: true, blue: true);    // bottom-right

        Assert.True(file.HasAudio);
        Assert.Equal(48000, file.AudioSampleRate);
        Assert.InRange(file.AudioSeconds, 2.7, 3.2);

        // A sine of amplitude 8000 has an RMS of about 5660.
        Assert.InRange(file.AudioRms, 4000, 7000);
    }

    [Fact]
    public void Several_streams_are_recorded_to_one_file_each_with_the_audio_in_every_file()
    {
        string path = Path.Combine(_directory, "multi.mp4");
        using (var rig = new Rig(path, "screen1", "screen2"))
        {
            rig.Recorder.Start();
            Assert.Equal(["multi-screen1.mp4", "multi-screen2.mp4"], rig.Recorder.GetStatus().Files.Select(Path.GetFileName));
            rig.Feed(frames: 30);
        }

        Assert.Equal(["multi-screen1.mp4", "multi-screen2.mp4"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
        foreach (string file in Directory.GetFiles(_directory))
        {
            Mp4File recording = Mp4File.Read(file);
            Assert.Equal(30, recording.VideoFrames);
            Assert.InRange(recording.AudioSeconds, 1.7, 2.2);
            Assert.InRange(recording.AudioRms, 4000, 7000);
        }
    }

    [Fact]
    public void Changing_the_streams_while_recording_continues_in_new_files()
    {
        string path = Path.Combine(_directory, "switch.mp4");
        using (var rig = new Rig(path, "desktop"))
        {
            rig.Recorder.Start();
            rig.Feed(frames: 15);

            rig.ReplaceSources("screen1", "screen2");
            Assert.Equal(RecordingState.Recording, rig.Recorder.State);
            rig.Feed(frames: 30);
        }

        Assert.Equal(["switch-2-screen1.mp4", "switch-2-screen2.mp4", "switch.mp4"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
        Assert.Equal(15, Mp4File.Read(path).VideoFrames);
        Assert.Equal(30, Mp4File.Read(Path.Combine(_directory, "switch-2-screen1.mp4")).VideoFrames);
        Assert.Equal(30, Mp4File.Read(Path.Combine(_directory, "switch-2-screen2.mp4")).VideoFrames);
    }

    [Fact]
    public void A_pause_is_cut_out_of_the_file()
    {
        string path = Path.Combine(_directory, "paused.mp4");
        using (var rig = new Rig(path))
        {
            rig.Recorder.Start();
            rig.Feed(frames: 30);          // 2 s recorded
            rig.Recorder.Pause();
            Assert.Equal(RecordingState.Paused, rig.Recorder.State);
            rig.Feed(frames: 45);          // 3 s that must not appear
            rig.Recorder.Resume();
            rig.Feed(frames: 30);          // 2 s recorded
        }

        Mp4File file = Mp4File.Read(path);

        Assert.Equal(60, file.VideoFrames);
        Assert.InRange(file.VideoSeconds, 3.9, 4.1);
        Assert.InRange(file.AudioSeconds, 3.7, 4.2);
        Assert.InRange(file.AudioRms, 4000, 7000);
    }

    [Fact]
    public void Pausing_or_resuming_a_stopped_recording_is_refused()
    {
        using var rig = new Rig(Path.Combine(_directory, "never.mp4"));

        Assert.Throws<RefusedException>(rig.Recorder.Pause);
        Assert.Throws<RefusedException>(rig.Recorder.Resume);
        Assert.Equal(RecordingState.Stopped, rig.Recorder.State);
    }

    [Fact]
    public void Every_start_makes_a_new_file()
    {
        string path = Path.Combine(_directory, "take.mp4");
        using (var rig = new Rig(path))
        {
            rig.Recorder.Start();
            rig.Feed(frames: 15);
            rig.Recorder.Stop();
            Assert.Equal(RecordingState.Stopped, rig.Recorder.State);

            rig.Feed(frames: 15);          // not recorded anywhere
            rig.Recorder.Start();
            rig.Feed(frames: 30);
        }

        Assert.Equal(["take-2.mp4", "take.mp4"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
        Assert.Equal(15, Mp4File.Read(path).VideoFrames);
        Assert.Equal(30, Mp4File.Read(Path.Combine(_directory, "take-2.mp4")).VideoFrames);
    }

    [Fact]
    public void A_segment_length_splits_the_recording_into_playable_files()
    {
        string path = Path.Combine(_directory, "long.mp4");
        using (var rig = new Rig(path, segmentLength: TimeSpan.FromSeconds(2)))
        {
            rig.Recorder.Start();
            rig.Feed(frames: 75); // five seconds -> 2 s + 2 s + 1 s
        }

        string?[] names = [.. Directory.GetFiles(_directory).Select(Path.GetFileName).Order()];
        Assert.Equal(["long-001.mp4", "long-002.mp4", "long-003.mp4"], names);

        int[] frames = [.. names.Select(name => Mp4File.Read(Path.Combine(_directory, name!)).VideoFrames)];
        Assert.Equal(75, frames.Sum());
        Assert.InRange(frames[0], 29, 32);
        Assert.InRange(frames[1], 29, 32);
    }

    [Fact]
    public void An_existing_file_is_not_overwritten_unless_asked()
    {
        string path = Path.Combine(_directory, "existing.mp4");
        File.WriteAllText(path, "not a video");
        RecordingSource[] sources = [new RecordingSource("screen1", () => TestFrames.Config, () => { })];

        using (var careful = new Mp4Recorder(_directory, path, null, overwrite: false, hasAudio: false))
        {
            careful.SetSources(sources);
            InvalidOperationException error = Assert.Throws<RefusedException>(careful.Start);
            Assert.Contains("--overwrite", error.Message);
            Assert.Equal(RecordingState.Stopped, careful.State);
        }

        using var overwriting = new Mp4Recorder(_directory, path, null, overwrite: true, hasAudio: false);
        overwriting.SetSources(sources);
        overwriting.Start();
        Assert.Equal(RecordingState.Recording, overwriting.State);
        overwriting.Stop();
    }

    [Fact]
    public void A_refused_start_does_not_use_up_a_name()
    {
        string path = Path.Combine(_directory, "take.mp4");
        File.WriteAllText(path, "not a video");
        using var recorder = new Mp4Recorder(_directory, path, null, overwrite: false, hasAudio: false);
        recorder.SetSources([new RecordingSource("screen1", () => TestFrames.Config, () => { })]);

        Assert.Throws<RefusedException>(recorder.Start);
        File.Delete(path); // the person moves the old file away and tries again

        recorder.Start();
        Assert.Equal(["take.mp4"], recorder.GetStatus().Files.Select(Path.GetFileName));
        recorder.Stop();
    }

    [Fact]
    public void Recordings_without_a_name_go_to_the_output_folder()
    {
        string folder = Path.Combine(_directory, "recordings");
        using (var rig = new Rig(new Mp4Recorder(folder, null, null, overwrite: false, hasAudio: true)))
        {
            Assert.Equal(folder, rig.Recorder.Folder);
            rig.Recorder.Start();
            rig.Feed(frames: 15);
        }

        // The folder did not exist; the recorder made it.
        string file = Assert.Single(Directory.GetFiles(folder));
        Assert.Matches(@"^capture-\d{8}-\d{6}\.mp4$", Path.GetFileName(file));
        Assert.Equal(15, Mp4File.Read(file).VideoFrames);
    }

    [Fact]
    public void A_relative_file_name_is_in_the_output_folder_and_a_full_path_is_not()
    {
        string folder = Path.Combine(_directory, "recordings");
        string elsewhere = Path.Combine(_directory, "elsewhere", "take.mp4");

        using var relative = new Mp4Recorder(folder, @"monday\take.mp4", null, overwrite: false, hasAudio: false);
        using var absolute = new Mp4Recorder(folder, elsewhere, null, overwrite: false, hasAudio: false);

        Assert.Equal(Path.Combine(folder, "monday"), relative.Folder);
        Assert.Equal(Path.GetDirectoryName(elsewhere), absolute.Folder);

        foreach (Mp4Recorder recorder in new[] { relative, absolute })
        {
            recorder.SetSources([new RecordingSource("screen1", () => TestFrames.Config, () => { })]);
            recorder.Start();
        }

        Assert.Equal([Path.Combine(folder, "monday", "take.mp4")], relative.GetStatus().Files);
        Assert.Equal([elsewhere], absolute.GetStatus().Files);
    }

    private static void AssertColour(Mp4File file, int x, int y, bool red, bool green, bool blue)
    {
        (int b, int g, int r) = file.FirstFramePixel(x, y);
        string actual = $"pixel ({x}, {y}) is R{r} G{g} B{b}";

        // Generous limits: H.264 is lossy and the decoder picks the YUV matrix; a swapped channel or
        // an upside-down picture misses them by a mile.
        Assert.True(red ? r > 170 : r < 90, actual);
        Assert.True(green ? g > 170 : g < 90, actual);
        Assert.True(blue ? b > 170 : b < 90, actual);
    }

    /// <summary>
    /// One converter and software encoder per stream, plus the recorder, fed with synthetic frames
    /// and a 440 Hz tone on a shared timeline.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly List<Stream> _streams = [];
        private int _nextVideoFrame;
        private long _nextAudioFrame;

        public Rig(string path, params string[] sourceIds)
            : this(path, null, sourceIds)
        {
        }

        public Rig(string path, TimeSpan? segmentLength, params string[] sourceIds)
            : this(new Mp4Recorder(Path.GetDirectoryName(path)!, path, segmentLength, overwrite: false, hasAudio: true), sourceIds)
        {
        }

        public Rig(Mp4Recorder recorder, params string[] sourceIds)
        {
            Recorder = recorder;
            ReplaceSources(sourceIds.Length > 0 ? sourceIds : ["screen1"]);
        }

        public Mp4Recorder Recorder { get; }

        /// <summary>Like a layout change: the old streams finish their last frames, then the new ones take over.</summary>
        public void ReplaceSources(params string[] sourceIds)
        {
            foreach (Stream stream in _streams)
            {
                stream.Dispose();
            }

            _streams.Clear();
            _streams.AddRange(sourceIds.Select(id => new Stream(id, Recorder)));
            Recorder.SetSources([.. _streams.Select(s => new RecordingSource(s.Id, () => TestFrames.Config, s.Encoder.RequestKeyFrame))]);
        }

        /// <summary>Advances the timeline by a number of video frames, delivering the audio that falls before each one first.</summary>
        public void Feed(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                long time = TestFrames.TimeOf(_nextVideoFrame);
                while (_nextAudioFrame * PcmFormat.FrameDuration < time)
                {
                    Recorder.WriteAudio(Tone(_nextAudioFrame++));
                }

                foreach (Stream stream in _streams)
                {
                    stream.Encode(_nextVideoFrame, time);
                }

                _nextVideoFrame++;
            }
        }

        public void Dispose()
        {
            foreach (Stream stream in _streams)
            {
                stream.Dispose();
            }

            Recorder.Dispose();
        }

        private static AudioFrame Tone(long frameIndex)
        {
            short[] samples = new short[PcmFormat.SamplesPerFrame];
            long first = frameIndex * PcmFormat.SamplesPerFrame;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(ToneAmplitude * Math.Sin(2 * Math.PI * ToneFrequency * (first + i) / PcmFormat.SampleRate));
            }

            return new AudioFrame { Samples = samples, Timestamp = frameIndex * PcmFormat.FrameDuration };
        }

        private sealed class Stream : IDisposable
        {
            private readonly VideoConverter _converter;

            public Stream(string id, Mp4Recorder recorder)
            {
                Id = id;
                _converter = new VideoConverter(TestFrames.Width, TestFrames.Height, TestFrames.Width, TestFrames.Height, TestFrames.Fps, MFMediaType.NV12, TestFrames.Config.Bt709);
                Encoder = H264Encoders.CreateSoftware(TestFrames.Config);
                Encoder.FrameEncoded += frame => recorder.WriteVideo(id, frame);
            }

            public string Id { get; }

            public H264Encoder Encoder { get; }

            public void Encode(int index, long time)
            {
                IMFSample screen = TestFrames.Create(index);
                IMFSample nv12 = _converter.Convert(screen, time, TestFrames.Config.FrameDuration);
                Mf.Release(screen);
                Encoder.Encode(nv12);
            }

            public void Dispose()
            {
                Encoder.Dispose();
                _converter.Dispose();
            }
        }
    }
}
