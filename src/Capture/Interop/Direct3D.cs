using System.Runtime.InteropServices;

namespace Capture.Interop;

/// <summary>
/// The few DXGI and Direct3D 11 calls that desktop duplication needs. The MediaFoundation wrapper
/// has no Direct3D interfaces, so these are called straight through the COM vtables; the slot
/// numbers are the method positions in dxgi.h, dxgi1_2.h, dxgi1_5.h, dxgi1_6.h and d3d11.h.
/// </summary>
internal static unsafe class Direct3D
{
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);

    public const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    public const int DXGI_FORMAT_R8G8B8A8_UNORM = 28;
    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int DXGI_MODE_ROTATION_UNSPECIFIED = 0;
    public const int DXGI_MODE_ROTATION_IDENTITY = 1;

    /// <summary>The colour space of an output in HDR mode (HDR10: BT.2020 primaries, PQ curve).</summary>
    public const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12;

    public const int D3D_FEATURE_LEVEL_11_0 = 0xb000;

    public const int D3D11_USAGE_DEFAULT = 0;
    public const int D3D11_USAGE_STAGING = 3;
    public const uint D3D11_BIND_CONSTANT_BUFFER = 0x4;
    public const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    public const uint D3D11_BIND_UNORDERED_ACCESS = 0x80;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const int D3D11_MAP_READ = 1;

    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_IDXGIOutput5 = new("80a07424-ab52-42eb-833c-0c42fd282d98");
    private static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public NativeMethods.RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTPUT_DESC1
    {
        public fixed char DeviceName[32];
        public NativeMethods.RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float RedPrimary[2];
        public fixed float GreenPrimary[2];
        public fixed float BluePrimary[2];
        public fixed float WhitePoint[2];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BUFFER_DESC
    {
        public uint ByteWidth;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_SUBRESOURCE_DATA
    {
        public IntPtr pSysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_DESC
    {
        public uint Width;
        public uint Height;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public int Format;
        public int ScanlineOrdering;
        public int Scaling;
        public int Rotation;
        public int DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public NativeMethods.POINT PointerPosition;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    public static int CreateFactory(out IntPtr factory)
    {
        Guid iid = IID_IDXGIFactory1;
        return CreateDXGIFactory1(&iid, out factory);
    }

    /// <summary>Creates a device on <paramref name="adapter"/>, or on the default graphics adapter when it is zero.</summary>
    public static int CreateDevice(IntPtr adapter, out IntPtr device, out IntPtr context, out int featureLevel)
    {
        int driverType = adapter == IntPtr.Zero ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_UNKNOWN;
        return D3D11CreateDevice(adapter, driverType, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out featureLevel, out context);
    }

    /// <summary>A device on WARP, Windows' software rasterizer: feature level 11, on any machine, with or without a graphics adapter.</summary>
    public static int CreateWarpDevice(out IntPtr device, out IntPtr context, out int featureLevel) =>
        D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out featureLevel, out context);

    /// <summary>Compiles HLSL source with the system's d3dcompiler_47.dll; throws with the compiler's messages on failure.</summary>
    public static byte[] CompileShader(string source, string entryPoint, string target)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(source);
        IntPtr code = IntPtr.Zero;
        IntPtr errors = IntPtr.Zero;
        try
        {
            int hr;
            fixed (byte* sourceText = text)
            {
                hr = D3DCompile(sourceText, (nuint)text.Length, null, IntPtr.Zero, IntPtr.Zero, entryPoint, target, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out code, out errors);
            }

            if (hr < 0)
            {
                string messages = errors == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors)).Trim('\0', ' ', '\r', '\n');
                throw new InvalidOperationException($"Shader compilation failed (0x{hr:X8}): {messages}");
            }

            return new ReadOnlySpan<byte>((void*)BlobPointer(code), (int)BlobSize(code)).ToArray();
        }
        finally
        {
            Release(ref code);
            Release(ref errors);
        }

        // ID3DBlob::GetBufferPointer and ::GetBufferSize
        static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Slot(blob, 3))(blob);
        static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)Slot(blob, 4))(blob);
    }

    public static void Release(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    // IDXGIFactory1::EnumAdapters1
    public static int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter)
    {
        fixed (IntPtr* result = &adapter)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, 12))(factory, index, result);
        }
    }

    // IDXGIAdapter::EnumOutputs
    public static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        fixed (IntPtr* result = &output)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(adapter, 7))(adapter, index, result);
        }
    }

    // IDXGIOutput::GetDesc
    public static int GetOutputDesc(IntPtr output, out DXGI_OUTPUT_DESC desc)
    {
        fixed (DXGI_OUTPUT_DESC* result = &desc)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)Slot(output, 7))(output, result);
        }
    }

    public static string GetDeviceName(in DXGI_OUTPUT_DESC desc)
    {
        fixed (char* name = desc.DeviceName)
        {
            return new string(name);
        }
    }

    // IUnknown::QueryInterface(IDXGIOutput1) + IDXGIOutput1::DuplicateOutput
    public static int DuplicateOutput(IntPtr output, IntPtr device, out IntPtr duplication)
    {
        duplication = IntPtr.Zero;
        Guid iid = IID_IDXGIOutput1;
        int hr = Marshal.QueryInterface(output, ref iid, out IntPtr output1);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            fixed (IntPtr* result = &duplication)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(output1, 22))(output1, device, result);
            }
        }
        finally
        {
            Marshal.Release(output1);
        }
    }

    // IUnknown::QueryInterface(IDXGIOutput6) + IDXGIOutput6::GetDesc1 (Windows 10 1803 and later)
    public static int GetOutputColorSpace(IntPtr output, out int colorSpace)
    {
        colorSpace = 0;
        Guid iid = IID_IDXGIOutput6;
        int hr = Marshal.QueryInterface(output, ref iid, out IntPtr output6);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            DXGI_OUTPUT_DESC1 desc;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC1*, int>)Slot(output6, 27))(output6, &desc);
            colorSpace = desc.ColorSpace;
            return hr;
        }
        finally
        {
            Marshal.Release(output6);
        }
    }

    // IUnknown::QueryInterface(IDXGIOutput5) + IDXGIOutput5::DuplicateOutput1: duplication that
    // hands over the desktop in one of the given formats instead of converting it to 8-bit BGRA.
    public static int DuplicateOutput1(IntPtr output, IntPtr device, ReadOnlySpan<int> formats, out IntPtr duplication)
    {
        duplication = IntPtr.Zero;
        Guid iid = IID_IDXGIOutput5;
        int hr = Marshal.QueryInterface(output, ref iid, out IntPtr output5);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            fixed (int* supported = formats)
            fixed (IntPtr* result = &duplication)
            {
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int*, IntPtr*, int>)Slot(output5, 26))(output5, device, 0, (uint)formats.Length, supported, result);
            }
        }
        finally
        {
            Marshal.Release(output5);
        }
    }

    // IDXGIOutputDuplication::GetDesc
    public static void GetDuplicationDesc(IntPtr duplication, out DXGI_OUTDUPL_DESC desc)
    {
        fixed (DXGI_OUTDUPL_DESC* result = &desc)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTDUPL_DESC*, void>)Slot(duplication, 7))(duplication, result);
        }
    }

    // IDXGIOutputDuplication::AcquireNextFrame
    public static int AcquireNextFrame(IntPtr duplication, uint timeoutMilliseconds, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource)
    {
        fixed (DXGI_OUTDUPL_FRAME_INFO* frameInfo = &info)
        fixed (IntPtr* result = &resource)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)Slot(duplication, 8))(duplication, timeoutMilliseconds, frameInfo, result);
        }
    }

    // IDXGIOutputDuplication::ReleaseFrame
    public static int ReleaseFrame(IntPtr duplication) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(duplication, 14))(duplication);

    // ID3D11Device::CreateBuffer
    public static int CreateBuffer(IntPtr device, in D3D11_BUFFER_DESC desc, out IntPtr buffer)
    {
        fixed (D3D11_BUFFER_DESC* description = &desc)
        fixed (IntPtr* result = &buffer)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_BUFFER_DESC*, void*, IntPtr*, int>)Slot(device, 3))(device, description, null, result);
        }
    }

    // ID3D11Device::CreateTexture2D
    public static int CreateTexture2D(IntPtr device, in D3D11_TEXTURE2D_DESC desc, out IntPtr texture) =>
        CreateTexture2D(device, desc, null, out texture);

    // ID3D11Device::CreateTexture2D, with initial contents
    public static int CreateTexture2D(IntPtr device, in D3D11_TEXTURE2D_DESC desc, D3D11_SUBRESOURCE_DATA* initialData, out IntPtr texture)
    {
        fixed (D3D11_TEXTURE2D_DESC* description = &desc)
        fixed (IntPtr* result = &texture)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Slot(device, 5))(device, description, initialData, result);
        }
    }

    // ID3D11Device::CreateShaderResourceView, viewing the whole resource in its own format
    public static int CreateShaderResourceView(IntPtr device, IntPtr resource, out IntPtr view)
    {
        fixed (IntPtr* result = &view)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Slot(device, 7))(device, resource, null, result);
        }
    }

    // ID3D11Device::CreateUnorderedAccessView, viewing the whole resource in its own format
    public static int CreateUnorderedAccessView(IntPtr device, IntPtr resource, out IntPtr view)
    {
        fixed (IntPtr* result = &view)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Slot(device, 8))(device, resource, null, result);
        }
    }

    // ID3D11Device::CreateComputeShader
    public static int CreateComputeShader(IntPtr device, ReadOnlySpan<byte> bytecode, out IntPtr shader)
    {
        fixed (byte* code = bytecode)
        fixed (IntPtr* result = &shader)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Slot(device, 18))(device, code, (nuint)bytecode.Length, IntPtr.Zero, result);
        }
    }

    public static int QueryTexture2D(IntPtr resource, out IntPtr texture)
    {
        Guid iid = IID_ID3D11Texture2D;
        return Marshal.QueryInterface(resource, ref iid, out texture);
    }

    // ID3D11Texture2D::GetDesc
    public static void GetTextureDesc(IntPtr texture, out D3D11_TEXTURE2D_DESC desc)
    {
        fixed (D3D11_TEXTURE2D_DESC* result = &desc)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Slot(texture, 10))(texture, result);
        }
    }

    // ID3D11DeviceContext::Dispatch
    public static void Dispatch(IntPtr context, uint groupsX, uint groupsY, uint groupsZ) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)Slot(context, 41))(context, groupsX, groupsY, groupsZ);

    // ID3D11DeviceContext::CopyResource
    public static void CopyResource(IntPtr context, IntPtr destination, IntPtr source) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(context, 47))(context, destination, source);

    // ID3D11DeviceContext::UpdateSubresource, replacing the whole of subresource 0
    public static void UpdateSubresource(IntPtr context, IntPtr resource, void* data) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Slot(context, 48))(context, resource, 0, null, data, 0, 0);

    // ID3D11DeviceContext::CSSetShaderResources, one view at slot 0 (zero unbinds)
    public static void CSSetShaderResource(IntPtr context, IntPtr view) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Slot(context, 67))(context, 0, 1, &view);

    // ID3D11DeviceContext::CSSetUnorderedAccessViews, one view at slot 0 (zero unbinds)
    public static void CSSetUnorderedAccessView(IntPtr context, IntPtr view) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)Slot(context, 68))(context, 0, 1, &view, null);

    // ID3D11DeviceContext::CSSetShader
    public static void CSSetShader(IntPtr context, IntPtr shader) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Slot(context, 69))(context, shader, null, 0);

    // ID3D11DeviceContext::CSSetConstantBuffers, one buffer at slot 0
    public static void CSSetConstantBuffer(IntPtr context, IntPtr buffer) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Slot(context, 71))(context, 0, 1, &buffer);

    // ID3D11DeviceContext::Map
    public static int Map(IntPtr context, IntPtr resource, int mapType, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        fixed (D3D11_MAPPED_SUBRESOURCE* result = &mapped)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Slot(context, 14))(context, resource, 0, mapType, 0, result);
        }
    }

    // ID3D11DeviceContext::Unmap
    public static void Unmap(IntPtr context, IntPtr resource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(context, 15))(context, resource, 0);

    private static void* Slot(IntPtr comObject, int index) => (*(void***)comObject)[index];

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(Guid* riid, out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int D3DCompile(
        byte* sourceData,
        nuint sourceDataSize,
        string? sourceName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);
}
