using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Capture.Interop;

namespace Capture.Video;

internal enum CaptureMethod
{
    /// <summary>Desktop duplication when it works, GDI otherwise.</summary>
    Auto,
    Dxgi,
    Gdi,
}

/// <summary>
/// Captures a <see cref="CaptureArea"/> (one screen, or several composed into one picture) into a
/// top-down 32-bit BGRX bitmap and draws the mouse pointer on top.
/// <para>
/// Media Foundation has no screen source, so the pixels come from the DXGI Desktop Duplication
/// API, one duplication per screen. Where that is unavailable (rotated screens, some remote and
/// virtual sessions, or while the display mode is changing) a GDI screen copy takes over for that
/// screen: slower, but it works everywhere.
/// </para>
/// </summary>
internal sealed class ScreenCapturer : IDisposable
{
    private static readonly long RetryInterval = Stopwatch.Frequency; // one second

    private readonly CaptureArea _area;
    private readonly bool _drawCursor;
    private readonly bool _useDuplication;
    private readonly Output[] _outputs;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previousBitmap;

    private IntPtr _hotspotCursor;
    private int _hotspotX;
    private int _hotspotY;
    private bool _disposed;

    public ScreenCapturer(CaptureArea area, bool drawCursor, CaptureMethod method)
    {
        _area = area;
        _drawCursor = drawCursor;
        _useDuplication = method != CaptureMethod.Gdi;
        Width = area.Width;
        Height = area.Height;
        _outputs = [.. area.Displays.Select(display => new Output(display, display.Left - area.Left, display.Top - area.Top))];

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not open the desktop device context.");
        }

        _memoryDc = NativeMethods.CreateCompatibleDC(_screenDc);

        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height, // negative height = top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };

        // A DIB section is plain memory that also has a device context, which is what lets GDI
        // draw the pointer onto a frame that came from Direct3D. It starts out all black.
        _bitmap = NativeMethods.CreateDIBSection(_memoryDc, ref header, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException($"Could not allocate a {Width}x{Height} capture bitmap.");
        }

        Pixels = bits;
        _previousBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);
        ClipToScreens();

        if (_useDuplication)
        {
            foreach (Output output in _outputs)
            {
                if (DesktopDuplication.IsHeld(output.Display))
                {
                    // The capturer this one replaces (after a layout change) still has the screen. It
                    // is disposed before this one starts capturing, so take the screen over then.
                    output.HandoverPending = true;
                    continue;
                }

                output.Duplication = DesktopDuplication.TryCreate(output.Display, out string reason);
                if (output.Duplication is not null)
                {
                    ReportHdr(output);
                    continue;
                }

                if (method == CaptureMethod.Dxgi)
                {
                    Dispose();
                    throw new InvalidOperationException($"Desktop duplication is not available for screen {output.Display.Number}: {reason}.");
                }

                Log.Info($"Desktop duplication is not available for screen {output.Display.Number} ({reason}); capturing it with GDI instead.");
                output.NextAttempt = Stopwatch.GetTimestamp() + RetryInterval;
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public int SizeInBytes => Stride * Height;

    /// <summary>The captured pixels: top-down BGRX, <see cref="Stride"/> bytes per row.</summary>
    public IntPtr Pixels { get; }

    public string Method
    {
        get
        {
            int duplicated = _outputs.Count(output => output.Duplication is not null || output.HandoverPending);
            return duplicated == _outputs.Length ? "DXGI desktop duplication"
                : duplicated == 0 ? "GDI"
                : "DXGI desktop duplication and GDI";
        }
    }

    /// <summary>
    /// Captures the area into <see cref="Pixels"/>. Returns false, leaving the previous picture in
    /// place where it could not be read, when the desktop cannot be read at all (lock screen, UAC
    /// prompt, session switch).
    /// </summary>
    public bool Capture(out int errorCode)
    {
        errorCode = 0;
        bool captured = true;
        long now = Stopwatch.GetTimestamp();

        // Queue the GPU copies of every screen first, then read them back, so the transfers overlap.
        foreach (Output output in _outputs)
        {
            output.Ready = UpdateDuplication(output, now);
        }

        foreach (Output output in _outputs)
        {
            if (output.Ready)
            {
                IntPtr destination = Pixels + output.Y * Stride + output.X * 4;
                if (output.Duplication!.Read(destination, Stride))
                {
                    continue;
                }

                LoseDuplication(output, now);
            }

            const int rop = NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT;
            DisplayInfo display = output.Display;
            if (!NativeMethods.BitBlt(_memoryDc, output.X, output.Y, display.Width, display.Height, _screenDc, display.Left, display.Top, rop))
            {
                errorCode = Marshal.GetLastWin32Error();
                captured = false;
            }
        }

        if (_drawCursor)
        {
            // Duplicated frames do not contain the pointer, and neither does a GDI copy.
            DrawCursor();
        }

        // GDI batches drawing calls; flush before the pixels are read directly.
        NativeMethods.GdiFlush();
        return captured;
    }

    public static string DescribeError(int errorCode) => new Win32Exception(errorCode).Message;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Output output in _outputs)
        {
            output.Duplication?.Dispose();
        }

        if (_memoryDc != IntPtr.Zero)
        {
            if (_previousBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memoryDc, _previousBitmap);
            }

            NativeMethods.DeleteDC(_memoryDc);
        }

        if (_bitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_bitmap);
        }

        if (_screenDc != IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
        }
    }

    /// <summary>
    /// Limits drawing to where screens are. Only the pointer is ever drawn outside a screen's own
    /// copy, and without this it would leave traces in the black areas no screen covers.
    /// </summary>
    private void ClipToScreens()
    {
        IntPtr region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        foreach (Output output in _outputs)
        {
            IntPtr rectangle = NativeMethods.CreateRectRgn(output.X, output.Y, output.X + output.Display.Width, output.Y + output.Display.Height);
            NativeMethods.CombineRgn(region, region, rectangle, NativeMethods.RGN_OR);
            NativeMethods.DeleteObject(rectangle);
        }

        NativeMethods.SelectClipRgn(_memoryDc, region); // the DC keeps its own copy
        NativeMethods.DeleteObject(region);
    }

    /// <summary>Returns true when the screen's duplicated image is ready to be read.</summary>
    private bool UpdateDuplication(Output output, long now)
    {
        if (!_useDuplication)
        {
            return false;
        }

        if (output.Duplication is null)
        {
            // Duplication is lost whenever the desktop changes under it; keep trying to get it back.
            if (now < output.NextAttempt)
            {
                return false;
            }

            output.Duplication = DesktopDuplication.TryCreate(output.Display, out string reason);
            bool handover = output.HandoverPending;
            output.HandoverPending = false;

            if (output.Duplication is null)
            {
                if (handover)
                {
                    Log.Info($"Desktop duplication is not available for screen {output.Display.Number} ({reason}); capturing it with GDI instead.");
                }

                output.NextAttempt = now + RetryInterval;
                return false;
            }

            if (!handover)
            {
                Log.Info($"Desktop duplication of screen {output.Display.Number} is available again.");
            }

            ReportHdr(output);
        }

        switch (output.Duplication.Update())
        {
            case DuplicationStatus.Ready:
                return true;
            case DuplicationStatus.NotReady:
                return false;
            default:
                LoseDuplication(output, now);
                return false;
        }
    }

    /// <summary>Says when a screen's HDR mode starts or stops mattering to its capture (HDR is switched in Windows' display settings).</summary>
    private static void ReportHdr(Output output)
    {
        DesktopDuplication duplication = output.Duplication!;
        bool hdr = duplication.IsHdr || duplication.HdrProblem is not null;
        if (hdr == output.Hdr)
        {
            return;
        }

        output.Hdr = hdr;
        int number = output.Display.Number;
        if (duplication.IsHdr)
        {
            Log.Info($"Screen {number} is in HDR mode; its picture is converted to SDR, with white at Windows' SDR content brightness ({duplication.SdrWhiteLevel:0} nits).");
        }
        else if (duplication.HdrProblem is not null)
        {
            Log.Warn($"Screen {number} is in HDR mode, but its picture cannot be converted to SDR ({duplication.HdrProblem}); it will look brighter and paler than on screen. --capture gdi avoids that, at a higher CPU cost.");
        }
        else
        {
            Log.Info($"Screen {number} is no longer in HDR mode.");
        }
    }

    private void LoseDuplication(Output output, long now)
    {
        Log.Info($"Desktop duplication of screen {output.Display.Number} was interrupted; capturing it with GDI until it is back.");
        output.Duplication?.Dispose();
        output.Duplication = null;
        output.NextAttempt = now + RetryInterval;
    }

    private void DrawCursor()
    {
        var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
        {
            return;
        }

        if (info.hCursor != _hotspotCursor)
        {
            // GetIconInfo hands back two bitmaps that must be freed, so only ask when the pointer shape changes.
            if (!NativeMethods.GetIconInfo(info.hCursor, out NativeMethods.ICONINFO icon))
            {
                return;
            }

            if (icon.hbmMask != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(icon.hbmColor);
            }

            _hotspotCursor = info.hCursor;
            _hotspotX = icon.xHotspot;
            _hotspotY = icon.yHotspot;
        }

        int x = info.ptScreenPos.X - _area.Left - _hotspotX;
        int y = info.ptScreenPos.Y - _area.Top - _hotspotY;
        NativeMethods.DrawIconEx(_memoryDc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
    }

    /// <summary>One screen of the area and where its picture goes in the bitmap.</summary>
    private sealed class Output(DisplayInfo display, int x, int y)
    {
        public DisplayInfo Display { get; } = display;

        public int X { get; } = x;

        public int Y { get; } = y;

        public DesktopDuplication? Duplication { get; set; }

        /// <summary>Another capturer had the screen when this one was made; the first capture takes it over.</summary>
        public bool HandoverPending { get; set; }

        /// <summary>Whether the screen was in HDR mode when last duplicated, to report changes.</summary>
        public bool Hdr { get; set; }

        public long NextAttempt { get; set; }

        public bool Ready { get; set; }
    }
}
