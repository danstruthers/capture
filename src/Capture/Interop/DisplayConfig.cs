using System.Runtime.InteropServices;

namespace Capture.Interop;

/// <summary>
/// Reads the "SDR content brightness" Windows uses for a screen in HDR mode, through the
/// DisplayConfig API: the screen's GDI name leads to its display path, and the path's target
/// (the monitor) has the white level.
/// </summary>
internal static unsafe class DisplayConfig
{
    /// <summary>Brightness of white in scRGB, where 1.0 is 80 nits.</summary>
    public const double ScRgbWhiteNits = 80;

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;
    private const int ModeInfoSize = 64; // sizeof(DISPLAYCONFIG_MODE_INFO); the modes are not read

    /// <summary>
    /// The brightness, in nits, at which Windows shows SDR white on the screen called
    /// <paramref name="gdiDeviceName"/> (\\.\DISPLAY1, ...). Null when Windows does not say
    /// (older than Windows 10 1709, or the screen is not active).
    /// </summary>
    public static double? GetSdrWhiteLevel(string gdiDeviceName)
    {
        // The configuration can change between asking for the sizes and reading it; then ask again.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
            {
                return null;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            byte[] modes = new byte[modeCount * ModeInfoSize];
            int result;
            fixed (DISPLAYCONFIG_PATH_INFO* pathInfo = paths)
            fixed (byte* modeInfo = modes)
            {
                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, pathInfo, ref modeCount, modeInfo, IntPtr.Zero);
            }

            if (result == ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            if (result != ERROR_SUCCESS)
            {
                return null;
            }

            foreach (DISPLAYCONFIG_PATH_INFO path in paths.AsSpan(0, (int)pathCount))
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        Type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        Size = sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                        AdapterId = path.SourceAdapterId,
                        Id = path.SourceId,
                    },
                };

                if (DisplayConfigGetDeviceInfo(&source.Header) != ERROR_SUCCESS
                    || !string.Equals(new string(source.ViewGdiDeviceName), gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        Type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                        Size = sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL),
                        AdapterId = path.TargetAdapterId,
                        Id = path.TargetId,
                    },
                };

                // The level is in thousandths of scRGB white: 1000 is 80 nits.
                return DisplayConfigGetDeviceInfo(&white.Header) == ERROR_SUCCESS && white.SdrWhiteLevel > 0
                    ? white.SdrWhiteLevel / 1000.0 * ScRgbWhiteNits
                    : null;
            }

            return null;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO with its source and target parts flattened (72 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public LUID SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIndex;
        public uint SourceStatusFlags;
        public LUID TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIndex;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int Type;
        public int Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint SdrWhiteLevel;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, DISPLAYCONFIG_PATH_INFO* paths, ref uint modeCount, byte* modes, IntPtr currentTopologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_HEADER* request);
}
