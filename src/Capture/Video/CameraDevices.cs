using System.Text;
using System.Text.RegularExpressions;
using Capture.Interop;
using MediaFoundation;

namespace Capture.Video;

/// <summary>A camera connected to this machine.</summary>
/// <param name="Id">Windows' symbolic link for the device: stable while it stays plugged into the same port.</param>
/// <param name="Name">Its name, as Windows shows it.</param>
/// <param name="Number">Its place in the list, from 1, for --camera and --list-devices.</param>
internal sealed record CameraDevice(string Id, string Name, int Number);

/// <summary>One way a camera can deliver pictures: a format, a size and a frame rate.</summary>
internal sealed record CameraMode(Guid Subtype, int Width, int Height, double Fps)
{
    /// <summary>"NV12", "MJPG", "YUY2".</summary>
    public string Format => CameraDevices.FourCc(Subtype);

    public override string ToString() => $"{Format} {Width}x{Height} at {Fps:0.##} fps";
}

/// <summary>Finding cameras, and choosing how to run one.</summary>
internal static partial class CameraDevices
{
    /// <summary>The stream id of the camera, in the state and in every request.</summary>
    public const string StreamId = "camera";

    /// <summary>Identifies the camera's pictures in binary messages; screens are 1 to 64, the desktop 0.</summary>
    public const int StreamNumber = 100;

    /// <summary>Formats in the order they are preferred, when a camera offers the same size and rate in several: uncompressed needs no decoding.</summary>
    private static readonly Guid[] PreferredFormats = [MFMediaType.NV12, MFMediaType.YUY2, MFMediaType.MJPG];

    /// <summary>The cameras connected now, in Windows' order.</summary>
    public static IReadOnlyList<CameraDevice> Enumerate()
    {
        IMFAttributes attributes = Mf.CreateAttributes(1);
        try
        {
            Mf.Check(attributes.SetGUID(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, CLSID.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID), "Set MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE");
            Mf.Check(MFExtern.MFEnumDeviceSources(attributes, out IMFActivate[] activates, out int count), "MFEnumDeviceSources");

            var cameras = new List<CameraDevice>(count);
            for (int i = 0; i < count; i++)
            {
                IMFActivate activate = activates[i];
                string? id = Mf.GetString(activate, MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                string? name = Mf.GetString(activate, MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                if (id is not null)
                {
                    cameras.Add(new CameraDevice(id, Tidy(name ?? "Camera"), cameras.Count + 1));
                }

                Mf.Release(activate);
            }

            return cameras;
        }
        finally
        {
            Mf.Release(attributes);
        }
    }

    /// <summary>
    /// The camera an option names: its number in --list-devices, its id, or (part of) its name.
    /// Throws <see cref="ArgumentException"/>, saying what is wrong, if that is not exactly one camera.
    /// </summary>
    public static CameraDevice Find(string selector) => Find(Enumerate(), selector);

    internal static CameraDevice Find(IReadOnlyList<CameraDevice> cameras, string selector)
    {
        string text = selector.Trim();
        if (int.TryParse(text, out int number))
        {
            return cameras.FirstOrDefault(camera => camera.Number == number)
                ?? throw new ArgumentException(cameras.Count == 0 ? "There is no camera." : $"Camera {number} does not exist.");
        }

        if (cameras.FirstOrDefault(camera => camera.Id.Equals(text, StringComparison.OrdinalIgnoreCase) || camera.Name.Equals(Tidy(text), StringComparison.CurrentCultureIgnoreCase)) is { } exact)
        {
            return exact;
        }

        List<CameraDevice> matches = [.. cameras.Where(camera => camera.Name.Contains(Tidy(text), StringComparison.CurrentCultureIgnoreCase))];
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No camera has a name containing \"{text}\"."),
            _ => throw new ArgumentException($"\"{text}\" matches {matches.Count} cameras ({string.Join("; ", matches.Select(m => m.Name))}). Use more of the name, or the camera number."),
        };
    }

    /// <summary>
    /// The mode to run a camera in for <paramref name="fps"/> frames a second, when its video may be
    /// at most <paramref name="maxWidth"/> by <paramref name="maxHeight"/>: among the modes that
    /// deliver at least that rate (or, when none does, the fastest rate there is), the one giving the
    /// largest video once scaled to fit, and of those the smallest picture, as a larger one would only
    /// be scaled down at a cost. Ties go to the preferred format, then to the rate nearest the one
    /// asked for.
    /// </summary>
    internal static CameraMode? ChooseMode(IReadOnlyList<CameraMode> modes, int fps, int maxWidth = int.MaxValue, int maxHeight = int.MaxValue)
    {
        if (modes.Count == 0)
        {
            return null;
        }

        // A rate of 29.97 counts as 30.
        double target = Math.Min(fps, modes.Max(mode => mode.Fps)) - 0.1;
        return modes
            .Where(mode => mode.Fps >= target)
            .OrderByDescending(mode => VideoArea(mode, maxWidth, maxHeight))
            .ThenBy(mode => (long)mode.Width * mode.Height)
            .ThenBy(mode => FormatRank(mode.Subtype))
            .ThenBy(mode => mode.Fps)
            .First();
    }

    /// <summary>The size of the video a mode gives: its picture, scaled down (keeping its shape) to fit the maximum.</summary>
    private static long VideoArea(CameraMode mode, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1, Math.Min(maxWidth / (double)mode.Width, maxHeight / (double)mode.Height));
        return (long)((int)Math.Round(mode.Width * scale) & ~1) * ((int)Math.Round(mode.Height * scale) & ~1);
    }

    /// <summary>A media subtype's four-character code: "NV12" for MFVideoFormat_NV12.</summary>
    internal static string FourCc(Guid subtype)
    {
        byte[] bytes = subtype.ToByteArray();
        string code = Encoding.ASCII.GetString(bytes, 0, 4);
        return code.All(c => c is >= ' ' and <= '~') ? code.TrimEnd() : subtype.ToString();
    }

    private static int FormatRank(Guid subtype)
    {
        int rank = Array.IndexOf(PreferredFormats, subtype);
        return rank < 0 ? PreferredFormats.Length : rank;
    }

    /// <summary>Windows' camera names can have runs of spaces in them ("USB  Live  Camera").</summary>
    private static string Tidy(string name) => Spaces().Replace(name.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
