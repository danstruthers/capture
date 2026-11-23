using System.Runtime.InteropServices;
using Capture.Interop;

namespace Capture.Video;

/// <summary>A screen and its position on the virtual desktop, in physical pixels.</summary>
internal sealed record DisplayInfo(int Index, string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary)
{
    /// <summary>The number people use for the screen: 1 for the first.</summary>
    public int Number => Index + 1;

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var displays = new List<DisplayInfo>();

        NativeMethods.MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                displays.Add(new DisplayInfo(
                    displays.Count,
                    info.szDevice,
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return displays;
    }

    /// <summary>
    /// The screens with the given numbers, or all of them when no numbers are given. Throws
    /// <see cref="ArgumentException"/> if a number names no screen.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> Select(IReadOnlyCollection<int>? numbers)
    {
        IReadOnlyList<DisplayInfo> displays = Enumerate();
        if (displays.Count == 0)
        {
            throw new InvalidOperationException("No screen is attached to this session.");
        }

        if (numbers is null || numbers.Count == 0)
        {
            return displays;
        }

        foreach (int number in numbers)
        {
            if (number < 1 || number > displays.Count)
            {
                string range = displays.Count == 1 ? "only screen 1" : $"screens 1 to {displays.Count}";
                throw new ArgumentException($"Screen {number} does not exist: this computer has {range}.");
            }
        }

        return displays.Where(display => numbers.Contains(display.Number)).ToList();
    }
}
