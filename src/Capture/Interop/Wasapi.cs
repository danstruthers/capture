using System.Runtime.InteropServices;

namespace Capture.Interop;

/// <summary>
/// The WASAPI calls needed for loopback capture (recording what a playback device is playing).
/// Media Foundation's device sources only cover recording devices, and the MediaFoundation wrapper
/// has no Core Audio interfaces, so these go through the COM vtables; the slot numbers are the
/// method positions in mmdeviceapi.h and audioclient.h.
/// </summary>
internal static unsafe class Wasapi
{
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private const uint CLSCTX_ALL = 23;
    private const int EDataFlowRender = 0;
    private const int ERoleConsole = 0;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;

    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>The fields of a WAVEFORMATEX / WAVEFORMATEXTENSIBLE that matter for describing PCM.</summary>
    public readonly record struct WaveFormat(int Channels, int SampleRate, int BitsPerSample, int BlockAlign, bool IsFloat, int ValidBitsPerSample, uint ChannelMask);

    public static int CreateDeviceEnumerator(out IntPtr enumerator)
    {
        Guid clsid = CLSID_MMDeviceEnumerator;
        Guid iid = IID_IMMDeviceEnumerator;
        return CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iid, out enumerator);
    }

    // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole)
    public static int GetDefaultPlaybackDevice(IntPtr enumerator, out IntPtr device)
    {
        fixed (IntPtr* result = &device)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, 4))(enumerator, EDataFlowRender, ERoleConsole, result);
        }
    }

    // IMMDevice::Activate(IAudioClient)
    public static int ActivateAudioClient(IntPtr device, out IntPtr audioClient)
    {
        Guid iid = IID_IAudioClient;
        fixed (IntPtr* result = &audioClient)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, void*, IntPtr*, int>)Slot(device, 3))(device, &iid, CLSCTX_ALL, null, result);
        }
    }

    // IAudioClient::GetMixFormat; the caller frees the format with Marshal.FreeCoTaskMem.
    public static int GetMixFormat(IntPtr audioClient, out IntPtr format)
    {
        fixed (IntPtr* result = &format)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(audioClient, 8))(audioClient, result);
        }
    }

    // IAudioClient::Initialize in shared loopback mode
    public static int InitializeLoopback(IntPtr audioClient, long bufferDuration, IntPtr format) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, Guid*, int>)Slot(audioClient, 3))(
            audioClient, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, bufferDuration, 0, format, null);

    // IAudioClient::Start
    public static int Start(IntPtr audioClient) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(audioClient, 10))(audioClient);

    // IAudioClient::Stop
    public static int Stop(IntPtr audioClient) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(audioClient, 11))(audioClient);

    // IAudioClient::GetService(IAudioCaptureClient)
    public static int GetCaptureClient(IntPtr audioClient, out IntPtr captureClient)
    {
        Guid iid = IID_IAudioCaptureClient;
        fixed (IntPtr* result = &captureClient)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(audioClient, 14))(audioClient, &iid, result);
        }
    }

    // IAudioCaptureClient::GetNextPacketSize
    public static int GetNextPacketSize(IntPtr captureClient, out uint frames)
    {
        fixed (uint* result = &frames)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(captureClient, 5))(captureClient, result);
        }
    }

    // IAudioCaptureClient::GetBuffer
    public static int GetBuffer(IntPtr captureClient, out IntPtr data, out uint frames, out uint flags, out ulong qpcPosition)
    {
        ulong devicePosition;
        fixed (IntPtr* dataPointer = &data)
        fixed (uint* frameCount = &frames)
        fixed (uint* bufferFlags = &flags)
        fixed (ulong* qpc = &qpcPosition)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, ulong*, ulong*, int>)Slot(captureClient, 3))(
                captureClient, dataPointer, frameCount, bufferFlags, &devicePosition, qpc);
        }
    }

    // IAudioCaptureClient::ReleaseBuffer
    public static int ReleaseBuffer(IntPtr captureClient, uint frames) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(captureClient, 4))(captureClient, frames);

    public static void Release(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    public static WaveFormat ReadFormat(IntPtr format)
    {
        byte* p = (byte*)format;
        ushort tag = *(ushort*)p;
        int channels = *(ushort*)(p + 2);
        int sampleRate = *(int*)(p + 4);
        int blockAlign = *(ushort*)(p + 12);
        int bits = *(ushort*)(p + 14);
        int extraSize = *(ushort*)(p + 16);

        bool isFloat = tag == WAVE_FORMAT_IEEE_FLOAT;
        int validBits = bits;
        uint channelMask = 0;

        if (tag == WAVE_FORMAT_EXTENSIBLE && extraSize >= 22)
        {
            validBits = *(ushort*)(p + 18);
            channelMask = *(uint*)(p + 20);
            isFloat = *(Guid*)(p + 24) == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        }

        return new WaveFormat(channels, sampleRate, bits, blockAlign, isFloat, validBits, channelMask);
    }

    private static void* Slot(IntPtr comObject, int index) => (*(void***)comObject)[index];

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint clsContext, Guid* iid, out IntPtr instance);
}
