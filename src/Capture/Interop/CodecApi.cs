using System.Runtime.InteropServices;

namespace Capture.Interop;

/// <summary>
/// ICodecAPI (strmif.h). The MediaFoundation wrapper package does not declare it, and it is the
/// only way to set an encoder MFT's rate control, GOP and low-latency mode or to force a key frame.
/// </summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported([In] ref Guid api);

    [PreserveSig] int IsModifiable([In] ref Guid api);

    [PreserveSig] int GetParameterRange([In] ref Guid api, out Variant valueMin, out Variant valueMax, out Variant steppingDelta);

    [PreserveSig] int GetParameterValues([In] ref Guid api, out IntPtr values, out int valuesCount);

    [PreserveSig] int GetDefaultValue([In] ref Guid api, out Variant value);

    [PreserveSig] int GetValue([In] ref Guid api, out Variant value);

    [PreserveSig] int SetValue([In] ref Guid api, [In] ref Variant value);

    [PreserveSig] int RegisterForEvent([In] ref Guid api, IntPtr userData);

    [PreserveSig] int UnregisterForEvent([In] ref Guid api);

    [PreserveSig] int SetAllDefaults();

    [PreserveSig] int SetValueWithNotify([In] ref Guid api, [In] ref Variant value, out IntPtr changedParam, out int changedParamCount);

    [PreserveSig] int SetAllDefaultsWithNotify(out IntPtr changedParam, out int changedParamCount);

    [PreserveSig] int GetAllSettings(IntPtr stream);

    [PreserveSig] int SetAllSettings(IntPtr stream);

    [PreserveSig] int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changedParam, out int changedParamCount);
}

/// <summary>A VARIANT restricted to the two value types the encoder properties below use.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    private const ushort VT_BOOL = 11;
    private const ushort VT_UI4 = 19;

    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public uint ulVal;
    [FieldOffset(8)] public short boolVal;

    public static Variant FromUInt32(uint value) => new() { vt = VT_UI4, ulVal = value };

    public static Variant FromBool(bool value) => new() { vt = VT_BOOL, boolVal = (short)(value ? -1 : 0) };
}

/// <summary>Encoder property GUIDs and values from codecapi.h.</summary>
internal static class CodecApiProperty
{
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const uint RateControlModeCbr = 0;
}
