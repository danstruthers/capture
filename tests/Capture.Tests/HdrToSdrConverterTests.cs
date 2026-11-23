using Capture.Interop;
using Capture.Video;

namespace Capture.Tests;

/// <summary>
/// Feeds the converter the scRGB values Windows composes SDR content into in HDR mode, and checks
/// that the original 8-bit values come back. They run on WARP, Windows' software rasterizer, so they
/// need no graphics adapter and give the same answer on every machine.
/// </summary>
public sealed unsafe class HdrToSdrConverterTests : IDisposable
{
    // Not multiples of the shader's 16 x 16 thread groups, so the edges are covered too.
    private const int Width = 262;
    private const int Height = 3;

    private IntPtr _device;
    private IntPtr _context;

    public HdrToSdrConverterTests()
    {
        int hr = Direct3D.CreateWarpDevice(out _device, out _context, out int featureLevel);
        Assert.True(hr >= 0 && featureLevel >= Direct3D.D3D_FEATURE_LEVEL_11_0, $"No feature level 11 WARP device (0x{hr:X8}, level 0x{featureLevel:X}).");
    }

    public void Dispose()
    {
        Direct3D.Release(ref _context);
        Direct3D.Release(ref _device);
    }

    [Theory]
    [InlineData(80)]   // SDR white at scRGB 1.0
    [InlineData(240)]  // this development machine's setting
    [InlineData(480)]
    public void Sdr_content_comes_back_with_the_values_it_was_drawn_with(double whiteLevel)
    {
        using var converter = new HdrToSdrConverter(_device, _context, Width, Height);
        byte[] picture = Convert(converter, Composed(whiteLevel), whiteLevel);

        for (int y = 0; y < Height; y++)
        {
            for (int level = 0; level < 256; level++)
            {
                (int red, int green, int blue) = DrawnAt(level);
                AssertPixel(picture, level, y, red, green, blue);
            }
        }
    }

    [Fact]
    public void Light_beyond_sdr_white_is_clipped_to_white_and_colours_outside_srgb_to_black()
    {
        using var converter = new HdrToSdrConverter(_device, _context, Width, Height);
        byte[] picture = Convert(converter, Composed(240), 240);

        AssertPixel(picture, 256, 0, 255, 255, 255); // twice as bright as SDR white
        AssertPixel(picture, 257, 0, 255, 255, 255); // an HDR highlight of 1000 nits
        AssertPixel(picture, 258, 0, 0, 0, 0);       // negative: a colour outside the sRGB gamut
        AssertPixel(picture, 259, 1, 255, 0, 0);     // pure red at SDR white
    }

    [Fact]
    public void A_change_of_white_level_applies_to_the_image_already_loaded()
    {
        using var converter = new HdrToSdrConverter(_device, _context, Width, Height);
        Convert(converter, Composed(240), 240);

        // Brighter SDR white: the same light is now half as bright relative to white.
        byte[] picture = ConvertAgain(converter, 480);

        foreach (int level in new[] { 0, 30, 128, 200, 255 })
        {
            int expected = Encode(Decode(level) / 2);
            Assert.InRange(picture[level * 4 + 2], expected - 1, expected + 1);
        }
    }

    /// <summary>The colour drawn at column <paramref name="level"/>: three different ramps, so a swapped channel shows.</summary>
    private static (int Red, int Green, int Blue) DrawnAt(int level) => (level, 255 - level, level * 7 % 256);

    /// <summary>
    /// The desktop as Windows composes it in HDR mode: the 8-bit colours decoded from sRGB to linear
    /// light and scaled so that white is <paramref name="whiteLevel"/> nits, as scRGB (1.0 = 80 nits).
    /// Columns 256 on are special values.
    /// </summary>
    private static float[,,] Composed(double whiteLevel)
    {
        double scale = whiteLevel / DisplayConfig.ScRgbWhiteNits;
        var pixels = new float[Height, Width, 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (double red, double green, double blue) = x switch
                {
                    < 256 => (Decode(DrawnAt(x).Red) * scale, Decode(DrawnAt(x).Green) * scale, Decode(DrawnAt(x).Blue) * scale),
                    256 => (2 * scale, 2 * scale, 2 * scale),
                    257 => (12.5, 12.5, 12.5),
                    258 => (-0.2, -0.1, -0.3),
                    _ => (scale, 0, 0),
                };

                pixels[y, x, 0] = (float)red;
                pixels[y, x, 1] = (float)green;
                pixels[y, x, 2] = (float)blue;
                pixels[y, x, 3] = 1;
            }
        }

        return pixels;
    }

    private static double Decode(int level)
    {
        double value = level / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static int Encode(double light) =>
        (int)Math.Round(255 * (light <= 0.0031308 ? light * 12.92 : 1.055 * Math.Pow(light, 1 / 2.4) - 0.055));

    private static void AssertPixel(byte[] picture, int x, int y, int red, int green, int blue)
    {
        int offset = (y * Width + x) * 4;
        (int b, int g, int r, int a) = (picture[offset], picture[offset + 1], picture[offset + 2], picture[offset + 3]);
        string actual = $"pixel ({x}, {y}) is R{r} G{g} B{b} A{a}, drawn as R{red} G{green} B{blue}";

        // Within one level: 16-bit floats and rounding; a wrong curve or scale misses by far more.
        Assert.True(Math.Abs(r - red) <= 1 && Math.Abs(g - green) <= 1 && Math.Abs(b - blue) <= 1 && a == 255, actual);
    }

    private byte[] Convert(HdrToSdrConverter converter, float[,,] desktop, double whiteLevel)
    {
        IntPtr texture = CreateDesktopTexture(desktop);
        try
        {
            converter.Load(texture);
        }
        finally
        {
            Direct3D.Release(ref texture);
        }

        return ConvertAgain(converter, whiteLevel);
    }

    private byte[] ConvertAgain(HdrToSdrConverter converter, double whiteLevel)
    {
        converter.Convert(whiteLevel);
        return ReadPicture(converter.Picture);
    }

    private IntPtr CreateDesktopTexture(float[,,] pixels)
    {
        ushort[] halves = new ushort[Height * Width * 4];
        int i = 0;
        foreach (float value in pixels)
        {
            halves[i++] = BitConverter.HalfToUInt16Bits((Half)value);
        }

        var desc = new Direct3D.D3D11_TEXTURE2D_DESC
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Direct3D.DXGI_FORMAT_R16G16B16A16_FLOAT,
            SampleCount = 1,
            Usage = Direct3D.D3D11_USAGE_DEFAULT,
            BindFlags = Direct3D.D3D11_BIND_SHADER_RESOURCE,
        };

        fixed (ushort* data = halves)
        {
            var initial = new Direct3D.D3D11_SUBRESOURCE_DATA { pSysMem = (IntPtr)data, SysMemPitch = Width * 8 };
            Assert.True(Direct3D.CreateTexture2D(_device, desc, &initial, out IntPtr texture) >= 0);
            return texture;
        }
    }

    /// <summary>Reads the converter's output back as BGRA bytes, Width * 4 per row.</summary>
    private byte[] ReadPicture(IntPtr picture)
    {
        var desc = new Direct3D.D3D11_TEXTURE2D_DESC
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Direct3D.DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleCount = 1,
            Usage = Direct3D.D3D11_USAGE_STAGING,
            CPUAccessFlags = Direct3D.D3D11_CPU_ACCESS_READ,
        };
        Assert.True(Direct3D.CreateTexture2D(_device, desc, out IntPtr staging) >= 0);

        try
        {
            Direct3D.CopyResource(_context, staging, picture);
            Assert.True(Direct3D.Map(_context, staging, Direct3D.D3D11_MAP_READ, out Direct3D.D3D11_MAPPED_SUBRESOURCE mapped) >= 0);
            try
            {
                byte[] bytes = new byte[Width * Height * 4];
                for (int y = 0; y < Height; y++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + y * mapped.RowPitch, Width * 4).CopyTo(bytes.AsSpan(y * Width * 4));
                }

                return bytes;
            }
            finally
            {
                Direct3D.Unmap(_context, staging);
            }
        }
        finally
        {
            Direct3D.Release(ref staging);
        }
    }
}
