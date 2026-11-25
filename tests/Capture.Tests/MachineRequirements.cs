using Capture.Audio;
using Capture.Interop;
using Capture.Video;

namespace Capture.Tests;

/// <summary>What a test needs from the machine it runs on, beyond Windows and Media Foundation.</summary>
[Flags]
public enum Needs
{
    /// <summary>An interactive desktop with at least one screen.</summary>
    Screen = 1,

    /// <summary>At least two screens.</summary>
    TwoScreens = 2,

    /// <summary>Screens that leave part of the desktop rectangle uncovered (screens of different sizes or offsets).</summary>
    DesktopWithGaps = 4,

    /// <summary>DXGI desktop duplication of the primary screen (not in services and some remote sessions).</summary>
    DesktopDuplication = 8,

    /// <summary>At least one audio device, recording or playback.</summary>
    AudioDevices = 16,

    /// <summary>A camera.</summary>
    Camera = 32,
}

/// <summary>A test that is skipped, saying why, on a machine without what it <see cref="Needs"/>.</summary>
public sealed class FactNeedingAttribute : FactAttribute
{
    public FactNeedingAttribute(Needs needs) => Skip = Machine.Lacks(needs);
}

/// <summary>A theory that is skipped, saying why, on a machine without what it <see cref="Needs"/>.</summary>
public sealed class TheoryNeedingAttribute : TheoryAttribute
{
    public TheoryNeedingAttribute(Needs needs) => Skip = Machine.Lacks(needs);
}

/// <summary>What this machine has, found out once per test run.</summary>
internal static class Machine
{
    private static readonly Lazy<IReadOnlyList<DisplayInfo>> s_displays = new(() =>
    {
        NativeMethods.EnableDpiAwareness();
        try
        {
            return DisplayInfo.Enumerate();
        }
        catch (Exception)
        {
            return [];
        }
    });

    private static readonly Lazy<bool> s_duplication = new(() =>
    {
        if (s_displays.Value.FirstOrDefault(display => display.IsPrimary) is not { } primary)
        {
            return false;
        }

        try
        {
            using var capturer = new ScreenCapturer(CaptureArea.ForScreen(primary), drawCursor: false, CaptureMethod.Dxgi);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    });

    private static readonly Lazy<bool> s_audioDevices = new(() =>
    {
        try
        {
            return AudioDevices.Enumerate().Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    private static readonly Lazy<IReadOnlyList<CameraDevice>> s_cameras = new(() =>
    {
        try
        {
            Mf.Startup();
            try
            {
                return CameraDevices.Enumerate();
            }
            finally
            {
                Mf.Shutdown();
            }
        }
        catch (Exception)
        {
            return [];
        }
    });

    public static IReadOnlyList<DisplayInfo> Displays => s_displays.Value;

    public static IReadOnlyList<CameraDevice> Cameras => s_cameras.Value;

    /// <summary>Why this machine cannot run a test that needs <paramref name="needs"/>, or null if it can.</summary>
    public static string? Lacks(Needs needs)
    {
        if (needs.HasFlag(Needs.Screen) && Displays.Count == 0)
        {
            return "needs a screen (an interactive desktop)";
        }

        if (needs.HasFlag(Needs.TwoScreens) && Displays.Count < 2)
        {
            return "needs two screens";
        }

        if (needs.HasFlag(Needs.DesktopWithGaps) && (Displays.Count == 0 || !HasGaps(CaptureArea.ForDesktop(Displays))))
        {
            return "needs screens that leave part of the desktop uncovered";
        }

        if (needs.HasFlag(Needs.DesktopDuplication) && !s_duplication.Value)
        {
            return "needs DXGI desktop duplication, which this session does not have";
        }

        if (needs.HasFlag(Needs.AudioDevices) && !s_audioDevices.Value)
        {
            return "needs an audio device";
        }

        if (needs.HasFlag(Needs.Camera) && Cameras.Count == 0)
        {
            return "needs a camera";
        }

        return null;
    }

    /// <summary>A point of the desktop rectangle that no screen covers, if there is one.</summary>
    public static (int X, int Y)? FindUncoveredPixel(CaptureArea area)
    {
        for (int y = 0; y < area.Height; y += 16)
        {
            for (int x = 0; x < area.Width; x += 16)
            {
                int desktopX = area.Left + x;
                int desktopY = area.Top + y;
                if (!area.Displays.Any(d => desktopX >= d.Left && desktopX < d.Right && desktopY >= d.Top && desktopY < d.Bottom))
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    private static bool HasGaps(CaptureArea area) => FindUncoveredPixel(area) is not null;
}
