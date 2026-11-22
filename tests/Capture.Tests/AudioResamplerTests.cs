using System.Runtime.InteropServices;
using Capture.Audio;
using Capture.Interop;
using MediaFoundation;

namespace Capture.Tests;

[Collection(MediaFoundationCollection.Name)]
public class AudioResamplerTests
{
    [Fact]
    public void Converts_48_kHz_stereo_float_to_8_kHz_mono_16_bit()
    {
        const int inputRate = 48000;
        const double amplitude = 0.5;

        Mf.Check(MFExtern.MFCreateMediaType(out IMFMediaType inputType), "MFCreateMediaType");
        inputType.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio);
        inputType.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, MFMediaType.Float);
        inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, 2);
        inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, inputRate);
        inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
        inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BLOCK_ALIGNMENT, 8);
        inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, inputRate * 8);
        inputType.SetUINT32(MFAttributesClsid.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        IMFMediaType outputType = Mf.CreatePcmType(PcmFormat.SampleRate, PcmFormat.Channels);

        var output = new List<short>();
        using (var resampler = new AudioResampler(inputType, outputType))
        {
            // Two seconds of a 440 Hz tone, identical in both channels, in 10 ms blocks.
            float[] block = new float[inputRate / 100 * 2];
            for (int n = 0; n < 200; n++)
            {
                for (int i = 0; i < block.Length / 2; i++)
                {
                    long sample = (long)n * (block.Length / 2) + i;
                    block[i * 2] = block[i * 2 + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * sample / inputRate));
                }

                resampler.Process(MemoryMarshal.AsBytes(block.AsSpan()), pcm => output.AddRange(MemoryMarshal.Cast<byte, short>(pcm)));
            }
        }

        Mf.Release(inputType);
        Mf.Release(outputType);

        // Two seconds at 8 kHz, less whatever the filter is still holding.
        Assert.InRange(output.Count, 15800, 16000);

        // The tone comes through at the same level: RMS of a 0.5 sine on a 16-bit scale is about 11585.
        double rms = Math.Sqrt(output.Skip(800).Average(sample => (double)sample * sample));
        Assert.InRange(rms, 10500, 12500);
    }
}
