using Capture.Interop;

namespace Capture.Video;

/// <summary>
/// Turns the picture of a screen in HDR mode back into the 8-bit SDR picture apps drew, on the GPU.
/// <para>
/// In HDR mode Windows composes the desktop in scRGB: linear light, BT.709 primaries, 1.0 = 80
/// nits, in 16-bit floats. SDR content is placed in it by decoding its sRGB values and scaling
/// them so that white is as bright as the "SDR content brightness" setting. This shader undoes
/// exactly that (divide by the white level, encode as sRGB), so SDR content comes out with the
/// values it was drawn with. HDR content brighter than SDR white is clipped to white.
/// </para>
/// <para>
/// The 8-bit BGRA that desktop duplication would otherwise give for an HDR screen does no such
/// scaling, which makes everything look too bright and washed out.
/// </para>
/// </summary>
internal sealed unsafe class HdrToSdrConverter : IDisposable
{
    private const int ThreadGroupSize = 16;

    // Writes BGRA into an RGBA texture: storing to a B8G8R8A8 view is optional for GPUs, R8G8B8A8
    // is not, and swapping the channels here gives the bytes the BGRA order the rest expects.
    private static readonly string ShaderSource = $$"""
        Texture2D<float4> Desktop : register(t0);
        RWTexture2D<unorm float4> Picture : register(u0);

        cbuffer Settings : register(b0)
        {
            float Scale; // 80 nits / SDR white level in nits
        };

        [numthreads({{ThreadGroupSize}}, {{ThreadGroupSize}}, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            uint width, height;
            Picture.GetDimensions(width, height);
            if (id.x >= width || id.y >= height)
            {
                return;
            }

            float3 light = saturate(Desktop[id.xy].rgb * Scale);
            float3 encoded = light <= 0.0031308 ? light * 12.92 : 1.055 * pow(light, 1.0 / 2.4) - 0.055;
            Picture[id.xy] = float4(encoded.b, encoded.g, encoded.r, 1);
        }
        """;

    private static readonly Lazy<byte[]> s_bytecode = new(() => Direct3D.CompileShader(ShaderSource, "main", "cs_5_0"));

    private readonly IntPtr _context;
    private readonly int _width;
    private readonly int _height;
    private IntPtr _desktop;
    private IntPtr _desktopView;
    private IntPtr _picture;
    private IntPtr _pictureView;
    private IntPtr _settings;
    private IntPtr _shader;
    private double _whiteLevel;

    /// <summary>Sets up the conversion of <paramref name="width"/> x <paramref name="height"/> pictures on a device of feature level 11.0 or later.</summary>
    public HdrToSdrConverter(IntPtr device, IntPtr context, int width, int height)
    {
        _context = context;
        _width = width;
        _height = height;

        try
        {
            Check(Direct3D.CreateComputeShader(device, s_bytecode.Value, out _shader), "create the conversion shader");

            var desktop = new Direct3D.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Direct3D.DXGI_FORMAT_R16G16B16A16_FLOAT,
                SampleCount = 1,
                Usage = Direct3D.D3D11_USAGE_DEFAULT,
                BindFlags = Direct3D.D3D11_BIND_SHADER_RESOURCE,
            };
            Check(Direct3D.CreateTexture2D(device, desktop, out _desktop), "create the HDR texture");
            Check(Direct3D.CreateShaderResourceView(device, _desktop, out _desktopView), "create the HDR texture view");

            var picture = desktop with { Format = Direct3D.DXGI_FORMAT_R8G8B8A8_UNORM, BindFlags = Direct3D.D3D11_BIND_UNORDERED_ACCESS };
            Check(Direct3D.CreateTexture2D(device, picture, out _picture), "create the SDR texture");
            Check(Direct3D.CreateUnorderedAccessView(device, _picture, out _pictureView), "create the SDR texture view");

            var settings = new Direct3D.D3D11_BUFFER_DESC { ByteWidth = 16, Usage = Direct3D.D3D11_USAGE_DEFAULT, BindFlags = Direct3D.D3D11_BIND_CONSTANT_BUFFER };
            Check(Direct3D.CreateBuffer(device, settings, out _settings), "create the shader settings");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The converted picture: an R8G8B8A8 texture whose bytes are in BGRA order. Copy it to a
    /// staging texture of the same format to read it.
    /// </summary>
    public IntPtr Picture => _picture;

    /// <summary>Copies a desktop image (R16G16B16A16 float, the converter's size) in, to be converted by <see cref="Convert"/>.</summary>
    public void Load(IntPtr desktopTexture) => Direct3D.CopyResource(_context, _desktop, desktopTexture);

    /// <summary>Converts the last loaded image into <see cref="Picture"/>, with SDR white at <paramref name="whiteLevel"/> nits.</summary>
    public void Convert(double whiteLevel)
    {
        if (whiteLevel != _whiteLevel)
        {
            float* settings = stackalloc float[4] { (float)(DisplayConfig.ScRgbWhiteNits / whiteLevel), 0, 0, 0 };
            Direct3D.UpdateSubresource(_context, _settings, settings);
            _whiteLevel = whiteLevel;
        }

        Direct3D.CSSetShader(_context, _shader);
        Direct3D.CSSetConstantBuffer(_context, _settings);
        Direct3D.CSSetShaderResource(_context, _desktopView);
        Direct3D.CSSetUnorderedAccessView(_context, _pictureView);
        Direct3D.Dispatch(_context, (uint)((_width + ThreadGroupSize - 1) / ThreadGroupSize), (uint)((_height + ThreadGroupSize - 1) / ThreadGroupSize), 1);

        // Unbound, the textures can be copied to and from freely.
        Direct3D.CSSetUnorderedAccessView(_context, IntPtr.Zero);
        Direct3D.CSSetShaderResource(_context, IntPtr.Zero);
    }

    public void Dispose()
    {
        Direct3D.Release(ref _settings);
        Direct3D.Release(ref _pictureView);
        Direct3D.Release(ref _picture);
        Direct3D.Release(ref _desktopView);
        Direct3D.Release(ref _desktop);
        Direct3D.Release(ref _shader);
    }

    private static void Check(int hr, string action)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"Could not {action} (0x{hr:X8}).");
        }
    }
}
