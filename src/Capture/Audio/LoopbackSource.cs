using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;

namespace Capture.Audio;

/// <summary>
/// Records "desktop audio": whatever the default playback device is playing, taken with a WASAPI
/// loopback stream and resampled to 8 kHz mono 16-bit PCM.
/// <para>
/// A loopback stream delivers nothing while nothing is playing. That is fine here: every packet is
/// placed on the timeline by its own timestamp, and the stretches in between read as silence.
/// </para>
/// </summary>
internal sealed unsafe class LoopbackSource : IAudioSource
{
    private const long DeviceBufferDuration = 2_000_000; // 200 ms, in 100 ns units
    private const int PollIntervalMs = 10;
    private const uint TimestampError = 0x4;

    private readonly MediaClock _clock;
    private readonly long _systemTimeOffset;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private Exception? _startError;
    private volatile bool _stopping;
    private bool _disposed;

    public LoopbackSource(MediaClock clock)
    {
        _clock = clock;

        // Packet timestamps are performance-counter times, the same clock Media Foundation uses.
        _systemTimeOffset = MFExtern.MFGetSystemTime() - clock.Now;

        // Everything, including opening the device, happens on one multithreaded-apartment thread.
        _thread = new Thread(Run) { IsBackground = true, Name = "desktop-audio-capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            _stopping = true;
            throw new TimeoutException("The playback device did not respond.");
        }

        if (_startError is not null)
        {
            _thread.Join();
            throw new InvalidOperationException(_startError.Message, _startError);
        }
    }

    public string Name { get; private set; } = "desktop audio";

    public AudioTimeline Timeline { get; } = new();

    public Exception? Failure { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0)
        {
            string detail = hr == unchecked((int)0x80070490) ? "there is no playback device" : $"error 0x{hr:X8}";
            throw new InvalidOperationException($"{operation} failed: {detail}.");
        }
    }

    private static AudioResampler CreateResampler(Wasapi.WaveFormat format)
    {
        Mf.Check(MFExtern.MFCreateMediaType(out IMFMediaType inputType), "MFCreateMediaType");
        IMFMediaType outputType = Mf.CreatePcmType(PcmFormat.SampleRate, PcmFormat.Channels);
        try
        {
            inputType.SetGUID(MFAttributesClsid.MF_MT_MAJOR_TYPE, MFMediaType.Audio);
            inputType.SetGUID(MFAttributesClsid.MF_MT_SUBTYPE, format.IsFloat ? MFMediaType.Float : MFMediaType.PCM);
            inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_NUM_CHANNELS, format.Channels);
            inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_SAMPLES_PER_SECOND, format.SampleRate);
            inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BITS_PER_SAMPLE, format.BitsPerSample);
            inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_BLOCK_ALIGNMENT, format.BlockAlign);
            inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, format.SampleRate * format.BlockAlign);
            inputType.SetUINT32(MFAttributesClsid.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

            if (format.ValidBitsPerSample != format.BitsPerSample)
            {
                inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_VALID_BITS_PER_SAMPLE, format.ValidBitsPerSample);
            }

            if (format.ChannelMask != 0)
            {
                // Tells the resampler which speaker each channel is, so surround layouts fold down to mono sensibly.
                inputType.SetUINT32(MFAttributesClsid.MF_MT_AUDIO_CHANNEL_MASK, unchecked((int)format.ChannelMask));
            }

            return new AudioResampler(inputType, outputType);
        }
        finally
        {
            Mf.Release(inputType);
            Mf.Release(outputType);
        }
    }

    private void Run()
    {
        IntPtr enumerator = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;
        IntPtr audioClient = IntPtr.Zero;
        IntPtr captureClient = IntPtr.Zero;
        IntPtr mixFormat = IntPtr.Zero;
        AudioResampler? resampler = null;

        try
        {
            Wasapi.WaveFormat format;
            try
            {
                Check(Wasapi.CreateDeviceEnumerator(out enumerator), "Opening the audio device list");
                Check(Wasapi.GetDefaultPlaybackDevice(enumerator, out device), "Finding the default playback device");
                Check(Wasapi.ActivateAudioClient(device, out audioClient), "Opening the playback device");
                Check(Wasapi.GetMixFormat(audioClient, out mixFormat), "Reading the playback format");
                format = Wasapi.ReadFormat(mixFormat);
                Check(Wasapi.InitializeLoopback(audioClient, DeviceBufferDuration, mixFormat), "Starting loopback capture");
                Check(Wasapi.GetCaptureClient(audioClient, out captureClient), "Starting loopback capture");
                resampler = CreateResampler(format);
                Check(Wasapi.Start(audioClient), "Starting loopback capture");
                Name = $"desktop audio ({format.SampleRate} Hz, {format.Channels} ch)";
            }
            catch (Exception ex)
            {
                _startError = ex;
                return;
            }
            finally
            {
                _started.Set();
            }

            while (!_stopping)
            {
                ReadPackets(captureClient, resampler, format);
                Thread.Sleep(PollIntervalMs);
            }

            Wasapi.Stop(audioClient);
        }
        catch (Exception ex)
        {
            if (!_stopping)
            {
                Failure = ex;
            }
        }
        finally
        {
            resampler?.Dispose();
            if (mixFormat != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormat);
            }

            Wasapi.Release(ref captureClient);
            Wasapi.Release(ref audioClient);
            Wasapi.Release(ref device);
            Wasapi.Release(ref enumerator);
        }
    }

    private void ReadPackets(IntPtr captureClient, AudioResampler resampler, Wasapi.WaveFormat format)
    {
        while (true)
        {
            Check(Wasapi.GetNextPacketSize(captureClient, out uint pending), "Reading desktop audio");
            if (pending == 0)
            {
                return;
            }

            Check(Wasapi.GetBuffer(captureClient, out IntPtr data, out uint frames, out uint flags, out ulong qpcPosition), "Reading desktop audio");
            try
            {
                // A packet flagged silent carries no usable data; leaving it out reads back as silence anyway.
                if (frames == 0 || (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                {
                    continue;
                }

                long duration = (long)frames * MediaClock.TicksPerSecond / format.SampleRate;
                long captureTime = (flags & TimestampError) != 0 || qpcPosition == 0
                    ? _clock.Now - duration
                    : (long)qpcPosition - _systemTimeOffset;

                var input = new ReadOnlySpan<byte>((void*)data, (int)frames * format.BlockAlign);
                resampler.Process(input, pcm =>
                {
                    ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
                    Timeline.Write(captureTime, samples);
                    captureTime += (long)samples.Length * MediaClock.TicksPerSecond / PcmFormat.SampleRate;
                });
            }
            finally
            {
                Wasapi.ReleaseBuffer(captureClient, frames);
            }
        }
    }
}
