using System.Runtime.InteropServices;

namespace Capture.Interop;

/// <summary>
/// The Core Audio calls needed to list the audio devices and for loopback capture (recording what a
/// playback device is playing). Media Foundation's device sources only cover recording devices, and
/// the MediaFoundation wrapper has no Core Audio interfaces, so these go through the COM vtables;
/// the slot numbers are the method positions in mmdeviceapi.h, propsys.h and audioclient.h.
/// </summary>
internal static unsafe class Wasapi
{
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    /// <summary>EDataFlow: playback devices.</summary>
    public const int EDataFlowRender = 0;

    /// <summary>EDataFlow: recording devices.</summary>
    public const int EDataFlowCapture = 1;

    public const uint DEVICE_STATE_ACTIVE = 0x1;

    /// <summary>HRESULT_FROM_WIN32(ERROR_NOT_FOUND): no such device, or no default device.</summary>
    public const int E_NOTFOUND = unchecked((int)0x80070490);

    /// <summary>The device was unplugged, disabled or reconfigured while in use.</summary>
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    private const uint CLSCTX_ALL = 23;
    private const int EDataFlowAll = 2;
    private const int ERoleConsole = 0;
    private const uint STGM_READ = 0;
    private const ushort VT_LPWSTR = 31;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;

    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IMMEndpoint = new("1BE09788-6894-4089-8586-9A2A6C265AC5");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    // PKEY_Device_FriendlyName: "Speakers (Realtek(R) Audio)", as Windows' sound settings show it.
    private static readonly Guid PKEY_Device_FriendlyName_FormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const uint PKEY_Device_FriendlyName_PropertyId = 14;

    /// <summary>The fields of a WAVEFORMATEX / WAVEFORMATEXTENSIBLE that matter for describing PCM.</summary>
    public readonly record struct WaveFormat(int Channels, int SampleRate, int BitsPerSample, int BlockAlign, bool IsFloat, int ValidBitsPerSample, uint ChannelMask);

    public static int CreateDeviceEnumerator(out IntPtr enumerator)
    {
        Guid clsid = CLSID_MMDeviceEnumerator;
        Guid iid = IID_IMMDeviceEnumerator;
        return CoCreateInstance(&clsid, IntPtr.Zero, CLSCTX_ALL, &iid, out enumerator);
    }

    // IMMDeviceEnumerator::EnumAudioEndpoints(eAll, stateMask); the collection holds both kinds.
    public static int EnumAudioEndpoints(IntPtr enumerator, uint stateMask, out IntPtr collection)
    {
        fixed (IntPtr* result = &collection)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, int>)Slot(enumerator, 3))(enumerator, EDataFlowAll, stateMask, result);
        }
    }

    // IMMDeviceEnumerator::GetDefaultAudioEndpoint(dataFlow, eConsole): the device Windows' sound settings call the default.
    public static int GetDefaultAudioEndpoint(IntPtr enumerator, int dataFlow, out IntPtr device)
    {
        fixed (IntPtr* result = &device)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, 4))(enumerator, dataFlow, ERoleConsole, result);
        }
    }

    // IMMDeviceEnumerator::GetDevice: also finds devices that are unplugged or disabled.
    public static int GetDevice(IntPtr enumerator, string id, out IntPtr device)
    {
        fixed (char* text = id)
        fixed (IntPtr* result = &device)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Slot(enumerator, 5))(enumerator, text, result);
        }
    }

    // IMMDeviceCollection::GetCount
    public static int GetCount(IntPtr collection, out uint count)
    {
        fixed (uint* result = &count)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(collection, 3))(collection, result);
        }
    }

    // IMMDeviceCollection::Item
    public static int GetItem(IntPtr collection, uint index, out IntPtr device)
    {
        fixed (IntPtr* result = &device)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(collection, 4))(collection, index, result);
        }
    }

    // IMMDevice::GetId: the endpoint ID string, which stays the same for as long as the device is installed.
    public static int GetId(IntPtr device, out string id)
    {
        char* text = null;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, char**, int>)Slot(device, 5))(device, &text);
        id = hr >= 0 && text != null ? new string(text) : string.Empty;
        if (text != null)
        {
            Marshal.FreeCoTaskMem((IntPtr)text);
        }

        return hr;
    }

    // IMMDevice::GetState: DEVICE_STATE_ACTIVE, _DISABLED, _NOTPRESENT or _UNPLUGGED.
    public static int GetState(IntPtr device, out uint state)
    {
        fixed (uint* result = &state)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(device, 6))(device, result);
        }
    }

    // IMMEndpoint::GetDataFlow: whether a device records (eCapture) or plays (eRender).
    public static int GetDataFlow(IntPtr device, out int dataFlow)
    {
        Guid iid = IID_IMMEndpoint;
        int hr = Marshal.QueryInterface(device, in iid, out IntPtr endpoint);
        if (hr < 0)
        {
            dataFlow = -1;
            return hr;
        }

        try
        {
            fixed (int* result = &dataFlow)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(endpoint, 3))(endpoint, result);
            }
        }
        finally
        {
            Marshal.Release(endpoint);
        }
    }

    // IMMDevice::OpenPropertyStore, then IPropertyStore::GetValue(PKEY_Device_FriendlyName).
    public static string? GetFriendlyName(IntPtr device)
    {
        IntPtr store;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(device, 4))(device, STGM_READ, &store) < 0)
        {
            return null;
        }

        try
        {
            var key = new PropertyKey { FormatId = PKEY_Device_FriendlyName_FormatId, PropertyId = PKEY_Device_FriendlyName_PropertyId };
            PropVariant value = default;
            if (((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)Slot(store, 5))(store, &key, &value) < 0)
            {
                return null;
            }

            try
            {
                return value.Type == VT_LPWSTR && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(&value);
            }
        }
        finally
        {
            Marshal.Release(store);
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

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(PropVariant* value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>A PROPVARIANT, of which only string values are read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public IntPtr Pointer;
    }
}
