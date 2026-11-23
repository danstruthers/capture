using Capture.Interop;
using Capture.Video;
using MediaFoundation;

namespace Capture.Tests;

[Collection(MediaFoundationCollection.Name)]
public class H264EncoderTests
{
    private const string SimulatedName = "simulated hardware encoder";

    private static H264Encoder CreateSimulated(out AsyncTransformSimulator simulator, bool producesOutput = true)
    {
        simulator = new AsyncTransformSimulator { ProducesOutput = producesOutput };
        return new H264Encoder(simulator, activate: null, SimulatedName, isHardware: true, TestFrames.Config);
    }

    /// <summary>Converts and encodes synthetic frames 0..count-1; returns the frames the encoder delivered, including those flushed on dispose.</summary>
    private static List<EncodedVideoFrame> Encode(H264Encoder encoder, int count, Action<int>? beforeFrame = null)
    {
        var delivered = new List<EncodedVideoFrame>();
        encoder.FrameEncoded += frame =>
        {
            lock (delivered)
            {
                delivered.Add(frame);
            }
        };

        using (var converter = VideoConverter.ToNv12(TestFrames.Width, TestFrames.Height, TestFrames.Width, TestFrames.Height, bt709: false))
        {
            for (int i = 0; i < count; i++)
            {
                beforeFrame?.Invoke(i);

                IMFSample screen = TestFrames.Create(i);
                IMFSample nv12 = converter.Convert(screen, TestFrames.TimeOf(i), TestFrames.Config.FrameDuration);
                Mf.Release(screen);
                encoder.Encode(nv12);

                if (encoder.IsAsync)
                {
                    // Feed at the pace the encoder works at, as live capture does, so no frame is dropped.
                    int expected = i + 1;
                    Assert.True(SpinWait.SpinUntil(() => CountOf(delivered) >= expected, TimeSpan.FromSeconds(5)), $"frame {i} was never delivered");
                }
            }
        }

        encoder.Dispose();
        return delivered;
    }

    private static int CountOf(List<EncodedVideoFrame> frames)
    {
        lock (frames)
        {
            return frames.Count;
        }
    }

    private static void AssertWellFormedStream(List<EncodedVideoFrame> frames, int expectedCount)
    {
        Assert.Equal(expectedCount, frames.Count);

        EncodedVideoFrame first = frames[0];
        Assert.True(first.IsKeyFrame, "the stream has to start with a key frame");
        Assert.NotNull(first.SequenceHeader);

        // A key frame carries its SPS and PPS ahead of the picture, so a decoder can start there.
        List<int> types = AnnexB.FindNals(first.Data).ConvertAll(nal => nal.Type);
        int picture = types.IndexOf(AnnexB.NalIdrSlice);
        Assert.True(picture > 0, "the key frame has no IDR slice");
        Assert.InRange(types.IndexOf(AnnexB.NalSps), 0, picture - 1);
        Assert.InRange(types.IndexOf(AnnexB.NalPps), 0, picture - 1);

        Assert.Equal(Enumerable.Range(0, expectedCount).Select(TestFrames.TimeOf), frames.Select(f => f.Timestamp));
        Assert.All(frames.Where(frame => !frame.IsKeyFrame), frame => Assert.Null(frame.SequenceHeader));
    }

    [Fact]
    public void Software_encoder_delivers_every_frame_in_order_starting_with_a_key_frame()
    {
        H264Encoder encoder = H264Encoders.CreateSoftware(TestFrames.Config);
        Assert.False(encoder.IsAsync);
        Assert.False(encoder.IsHardware);

        List<EncodedVideoFrame> frames = Encode(encoder, 20);

        AssertWellFormedStream(frames, 20);
        Assert.Single(frames, frame => frame.IsKeyFrame); // 20 frames is less than one GOP
    }

    [Fact]
    public void Software_encoder_makes_a_key_frame_on_request()
    {
        H264Encoder encoder = H264Encoders.CreateSoftware(TestFrames.Config);

        List<EncodedVideoFrame> frames = Encode(encoder, 20, beforeFrame: i =>
        {
            if (i == 12)
            {
                encoder.RequestKeyFrame();
            }
        });

        Assert.Equal([0, 12], Enumerable.Range(0, frames.Count).Where(i => frames[i].IsKeyFrame));
        Assert.NotNull(frames[12].SequenceHeader);
    }

    [Fact]
    public void Asynchronous_encoder_is_unlocked_and_delivers_every_frame_in_order()
    {
        H264Encoder encoder = CreateSimulated(out AsyncTransformSimulator simulator);
        Assert.True(encoder.IsAsync);
        Assert.True(simulator.WasUnlocked);

        List<EncodedVideoFrame> frames = Encode(encoder, 20);

        AssertWellFormedStream(frames, 20);
        Assert.Equal(20, simulator.InputsAccepted);
        Assert.Equal(0, encoder.DroppedInputs);
        Assert.True(simulator.WasShutDown, "disposing has to shut the transform down so its event thread ends");
    }

    [Fact]
    public void Asynchronous_encoder_makes_a_key_frame_on_request()
    {
        H264Encoder encoder = CreateSimulated(out AsyncTransformSimulator simulator);

        List<EncodedVideoFrame> frames = Encode(encoder, 20, beforeFrame: i =>
        {
            if (i == 7)
            {
                encoder.RequestKeyFrame();
            }
        });

        Assert.Equal(1, simulator.KeyFrameRequests);
        Assert.Equal([0, 7], Enumerable.Range(0, frames.Count).Where(i => frames[i].IsKeyFrame));
    }

    [Fact]
    public void Self_test_passes_on_a_working_asynchronous_encoder()
    {
        using H264Encoder encoder = CreateSimulated(out _);

        encoder.SelfTest();
    }

    [Fact]
    public void Self_test_fails_when_the_encoder_never_produces_output()
    {
        using H264Encoder encoder = CreateSimulated(out _, producesOutput: false);

        // Either the self-test's own deadline or the encoder's stall detection ends it; both mean "no output".
        Exception failure = Assert.ThrowsAny<Exception>(encoder.SelfTest);
        Assert.IsType<TimeoutException>(failure.InnerException ?? failure);
    }

    [Fact]
    public void An_encoder_that_stops_producing_output_is_reported_as_failed()
    {
        using H264Encoder encoder = CreateSimulated(out _, producesOutput: false);
        using var converter = VideoConverter.ToNv12(TestFrames.Width, TestFrames.Height, TestFrames.Width, TestFrames.Height, bt709: false);

        IMFSample NextFrame(int i)
        {
            IMFSample screen = TestFrames.Create(i);
            IMFSample nv12 = converter.Convert(screen, TestFrames.TimeOf(i), TestFrames.Config.FrameDuration);
            Mf.Release(screen);
            return nv12;
        }

        // Three seconds' worth of frames with nothing coming back is the limit.
        int limit = TestFrames.Fps * 3;
        Exception? failure = null;
        int submitted = 0;
        while (failure is null && submitted < limit * 2)
        {
            failure = Record.Exception(() => encoder.Encode(NextFrame(submitted)));
            submitted++;
        }

        Assert.IsType<InvalidOperationException>(failure);
        Assert.InRange(submitted, limit, limit + 2);

        // Once failed it stays failed, which is what lets the pipeline swap in the software encoder.
        Assert.Throws<InvalidOperationException>(() => encoder.Encode(NextFrame(submitted)));
    }
}
