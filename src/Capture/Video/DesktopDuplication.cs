using System.Collections.Concurrent;
using System.Diagnostics;
using Capture.Interop;

namespace Capture.Video;

internal enum DuplicationStatus
{
    /// <summary>An image is ready to read.</summary>
    Ready,

    /// <summary>The first image has not arrived yet.</summary>
    NotReady,

    /// <summary>Duplication stopped (display mode change, secure desktop, session switch) and has to be set up again.</summary>
    Lost,
}

/// <summary>
/// Reads one monitor's desktop image with the DXGI Desktop Duplication API: the compositor hands
/// over its current frame as a GPU texture, which is copied to a staging texture and read back.
/// Far cheaper than a GDI screen grab, but not available everywhere, so creation can fail.
/// <para>
/// A screen in HDR mode is duplicated in its own 16-bit floating-point format and converted back
/// to SDR on the GPU (<see cref="HdrToSdrConverter"/>); the 8-bit image DXGI would otherwise
/// make of it is far too bright.
/// </para>
/// </summary>
internal sealed unsafe class DesktopDuplication : IDisposable
{
    /// <summary>How often the SDR content brightness of an HDR screen is read again, so that changes to the setting take effect.</summary>
    private static readonly long WhiteLevelInterval = 2 * Stopwatch.Frequency;

    /// <summary>
    /// Screens this process is duplicating. A process can duplicate a screen only once at a time:
    /// DuplicateOutput fails with E_INVALIDARG when asked again, and only after a wait of several seconds.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> s_held = new(StringComparer.OrdinalIgnoreCase);

    private readonly DisplayInfo _display;
    private readonly int _width;
    private readonly int _height;
    private string? _heldScreen;
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _duplication;
    private IntPtr _staging;
    private HdrToSdrConverter? _converter;
    private long _nextWhiteLevelCheck;
    private bool _hasImage;

    private DesktopDuplication(DisplayInfo display)
    {
        _display = display;
        _width = display.Width;
        _height = display.Height;
    }

    /// <summary>True when the screen is in HDR mode and its picture is being converted to SDR.</summary>
    public bool ConvertsHdr => _converter is not null;

    /// <summary>For a screen in HDR mode: the brightness of SDR white in nits, which the conversion maps to full white.</summary>
    public double SdrWhiteLevel { get; private set; }

    /// <summary>Set when the screen is in HDR mode but its picture cannot be converted, saying why; it then looks too bright.</summary>
    public string? HdrProblem { get; private set; }

    /// <summary>Whether this process is already duplicating <paramref name="display"/>, so that another duplication of it cannot be made until that one is disposed.</summary>
    public static bool IsHeld(DisplayInfo display) => s_held.ContainsKey(display.DeviceName);

    /// <summary>Starts duplicating <paramref name="display"/>; returns null and a reason when that is not possible.</summary>
    public static DesktopDuplication? TryCreate(DisplayInfo display, out string reason)
    {
        // Claimed before trying, in one step, so that two streams can never both try the same screen.
        if (!s_held.TryAdd(display.DeviceName, true))
        {
            reason = "this program is already duplicating it";
            return null;
        }

        var duplication = new DesktopDuplication(display) { _heldScreen = display.DeviceName };
        try
        {
            duplication.Initialize();
            reason = string.Empty;
            return duplication;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        duplication.Dispose(); // gives the claim back
        return null;
    }

    /// <summary>
    /// Picks up the screen's current image and queues its copy to the CPU-readable texture, without
    /// waiting for the copy. Calling this for every screen before <see cref="Read"/> lets the
    /// transfers of several screens overlap.
    /// </summary>
    public DuplicationStatus Update()
    {
        bool whiteLevelChanged = RefreshWhiteLevel();

        // The first image can take a moment to arrive; afterwards just pick up whatever is new.
        uint timeout = _hasImage ? 0u : 250u;
        int hr = Direct3D.AcquireNextFrame(_duplication, timeout, out Direct3D.DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource);

        if (hr == Direct3D.DXGI_ERROR_WAIT_TIMEOUT)
        {
            if (!_hasImage)
            {
                return DuplicationStatus.NotReady;
            }

            // Nothing on screen changed, so the staging texture still holds the current image,
            // unless an HDR screen's white level changed and the last image needs converting again.
            if (whiteLevelChanged)
            {
                ConvertToStaging();
            }

            return DuplicationStatus.Ready;
        }

        if (hr < 0)
        {
            return DuplicationStatus.Lost;
        }

        try
        {
            // LastPresentTime is zero when only the mouse pointer moved and the image is unchanged.
            if (info.LastPresentTime != 0 || !_hasImage || whiteLevelChanged)
            {
                if (Direct3D.QueryTexture2D(resource, out IntPtr texture) < 0)
                {
                    return DuplicationStatus.Lost;
                }

                try
                {
                    if (_converter is null)
                    {
                        Direct3D.CopyResource(_context, _staging, texture);
                    }
                    else
                    {
                        // The format was settled when duplication started; should it ever change,
                        // start over rather than copy between textures that do not match.
                        Direct3D.GetTextureDesc(texture, out Direct3D.D3D11_TEXTURE2D_DESC desc);
                        if (desc.Format != Direct3D.DXGI_FORMAT_R16G16B16A16_FLOAT)
                        {
                            return DuplicationStatus.Lost;
                        }

                        _converter.Load(texture);
                        ConvertToStaging();
                    }
                }
                finally
                {
                    Direct3D.Release(ref texture);
                }

                _hasImage = true;
            }
        }
        finally
        {
            Direct3D.Release(ref resource);
            Direct3D.ReleaseFrame(_duplication);
        }

        return DuplicationStatus.Ready;
    }

    /// <summary>
    /// Writes the image picked up by the last successful <see cref="Update"/> to
    /// <paramref name="destination"/> as top-down BGRX rows. Returns false when duplication has
    /// been lost; the instance is then finished and a new one has to be created.
    /// </summary>
    public bool Read(IntPtr destination, int destinationStride) => _hasImage && ReadStaging(destination, destinationStride);

    public void Dispose()
    {
        _converter?.Dispose();
        _converter = null;
        Direct3D.Release(ref _staging);
        Direct3D.Release(ref _duplication);
        Direct3D.Release(ref _context);
        Direct3D.Release(ref _device);

        if (_heldScreen is not null)
        {
            s_held.TryRemove(_heldScreen, out _);
            _heldScreen = null;
        }
    }

    private static string Describe(int hr) => hr switch
    {
        Direct3D.E_ACCESSDENIED => "access denied (secure desktop or protected session)",
        Direct3D.DXGI_ERROR_UNSUPPORTED => "not supported for this display or session",
        Direct3D.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE => "too many applications are already duplicating the desktop",
        _ => $"error 0x{hr:X8}",
    };

    /// <summary>Throws an <see cref="InvalidOperationException"/> if a Direct3D call failed, saying which (when <paramref name="what"/> is given) and why.</summary>
    private static void Check(int hr, string? what = null)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException(what is null ? Describe(hr) : $"{what} ({Describe(hr)})");
        }
    }

    /// <summary>Sets duplication up. Throws <see cref="InvalidOperationException"/> saying why it cannot be used.</summary>
    private void Initialize()
    {
        IntPtr adapter = IntPtr.Zero;
        IntPtr output = IntPtr.Zero;
        try
        {
            if (!FindOutput(_display.DeviceName, out adapter, out output))
            {
                throw new InvalidOperationException("the monitor is not driven by a DXGI adapter");
            }

            // The device has to live on the adapter that drives the monitor.
            Check(Direct3D.CreateDevice(adapter, out _device, out _context, out int featureLevel), "Direct3D 11 device creation failed");

            if (Direct3D.GetOutputColorSpace(output, out int colorSpace) >= 0 && colorSpace == Direct3D.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)
            {
                try
                {
                    StartHdrDuplication(output, featureLevel);
                }
                catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
                {
                    HdrProblem = ex.Message; // falls back to the 8-bit image, below
                }
            }

            if (_duplication == IntPtr.Zero)
            {
                Check(Direct3D.DuplicateOutput(output, _device, out _duplication));
            }

            Direct3D.GetDuplicationDesc(_duplication, out Direct3D.DXGI_OUTDUPL_DESC desc);
            if (desc.Rotation is not (Direct3D.DXGI_MODE_ROTATION_IDENTITY or Direct3D.DXGI_MODE_ROTATION_UNSPECIFIED))
            {
                throw new InvalidOperationException("the monitor is rotated");
            }

            if (desc.Width != _width || desc.Height != _height)
            {
                throw new InvalidOperationException($"unexpected surface size {desc.Width}x{desc.Height}");
            }

            // The converter writes RGBA-ordered textures holding BGRA bytes; a straight copy is BGRA.
            var staging = new Direct3D.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = _converter is null ? Direct3D.DXGI_FORMAT_B8G8R8A8_UNORM : Direct3D.DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleCount = 1,
                Usage = Direct3D.D3D11_USAGE_STAGING,
                CPUAccessFlags = Direct3D.D3D11_CPU_ACCESS_READ,
            };

            Check(Direct3D.CreateTexture2D(_device, staging, out _staging), "staging texture creation failed");
        }
        finally
        {
            Direct3D.Release(ref output);
            Direct3D.Release(ref adapter);
        }
    }

    /// <summary>
    /// Duplicates an HDR screen in its 16-bit floating-point format and sets up the conversion to
    /// SDR. Throws saying why that is not possible, leaving nothing set up; the caller then falls
    /// back to the 8-bit image.
    /// </summary>
    private void StartHdrDuplication(IntPtr output, int featureLevel)
    {
        if (featureLevel < Direct3D.D3D_FEATURE_LEVEL_11_0)
        {
            throw new InvalidOperationException("the graphics adapter is older than Direct3D 11");
        }

        _converter = new HdrToSdrConverter(_device, _context, _width, _height);
        int hr = Direct3D.DuplicateOutput1(output, _device, [Direct3D.DXGI_FORMAT_R16G16B16A16_FLOAT], out _duplication);
        if (hr >= 0)
        {
            Direct3D.GetDuplicationDesc(_duplication, out Direct3D.DXGI_OUTDUPL_DESC desc);
            if (desc.Format == Direct3D.DXGI_FORMAT_R16G16B16A16_FLOAT)
            {
                RefreshWhiteLevel();
                return;
            }

            Direct3D.Release(ref _duplication);
            hr = Direct3D.DXGI_ERROR_UNSUPPORTED;
        }

        _converter.Dispose();
        _converter = null;
        throw new InvalidOperationException($"16-bit duplication is not available: {Describe(hr)}");
    }

    /// <summary>For an HDR screen, reads the SDR content brightness every couple of seconds; true when it changed.</summary>
    private bool RefreshWhiteLevel()
    {
        long now = Stopwatch.GetTimestamp();
        if (_converter is null || now < _nextWhiteLevelCheck)
        {
            return false;
        }

        _nextWhiteLevelCheck = now + WhiteLevelInterval;

        // Without an answer, assume Windows' default: SDR white at scRGB 1.0.
        double whiteLevel = DisplayConfig.GetSdrWhiteLevel(_display.DeviceName) ?? DisplayConfig.ScRgbWhiteNits;
        if (whiteLevel == SdrWhiteLevel)
        {
            return false;
        }

        bool first = SdrWhiteLevel == 0;
        SdrWhiteLevel = whiteLevel;
        if (!first)
        {
            Log.Info($"Screen {_display.Number}: SDR content brightness is now {whiteLevel:0} nits.");
        }

        return true;
    }

    private void ConvertToStaging()
    {
        _converter!.Convert(SdrWhiteLevel);
        Direct3D.CopyResource(_context, _staging, _converter.Picture);
    }

    private static bool FindOutput(string deviceName, out IntPtr adapter, out IntPtr output)
    {
        adapter = IntPtr.Zero;
        output = IntPtr.Zero;

        if (Direct3D.CreateFactory(out IntPtr factory) < 0)
        {
            return false;
        }

        try
        {
            for (uint a = 0; Direct3D.EnumAdapters(factory, a, out IntPtr candidateAdapter) >= 0; a++)
            {
                for (uint o = 0; Direct3D.EnumOutputs(candidateAdapter, o, out IntPtr candidateOutput) >= 0; o++)
                {
                    if (Direct3D.GetOutputDesc(candidateOutput, out Direct3D.DXGI_OUTPUT_DESC desc) >= 0
                        && string.Equals(Direct3D.GetDeviceName(desc), deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        adapter = candidateAdapter;
                        output = candidateOutput;
                        return true;
                    }

                    Direct3D.Release(ref candidateOutput);
                }

                Direct3D.Release(ref candidateAdapter);
            }

            return false;
        }
        finally
        {
            Direct3D.Release(ref factory);
        }
    }

    private bool ReadStaging(IntPtr destination, int destinationStride)
    {
        // Map waits for the GPU to finish the copy queued above; this read-back is the bulk of the capture time.
        if (Direct3D.Map(_context, _staging, Direct3D.D3D11_MAP_READ, out Direct3D.D3D11_MAPPED_SUBRESOURCE mapped) < 0)
        {
            return false;
        }

        try
        {
            int rowBytes = _width * 4;
            byte* source = (byte*)mapped.pData;
            byte* target = (byte*)destination;

            if (mapped.RowPitch == destinationStride)
            {
                long total = (long)destinationStride * _height;
                Buffer.MemoryCopy(source, target, total, total);
            }
            else
            {
                for (int y = 0; y < _height; y++)
                {
                    Buffer.MemoryCopy(source + (long)y * mapped.RowPitch, target + (long)y * destinationStride, rowBytes, rowBytes);
                }
            }

            return true;
        }
        finally
        {
            Direct3D.Unmap(_context, _staging);
        }
    }
}
