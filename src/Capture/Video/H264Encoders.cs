using Capture.Interop;
using MediaFoundation;
using MediaFoundation.Transform;

namespace Capture.Video;

/// <summary>
/// The H.264 encoders of this machine: which hardware encoders are installed, and choosing the
/// encoder for a stream.
/// </summary>
internal static class H264Encoders
{
    public const string SoftwareEncoderName = "Microsoft H264 Encoder MFT";

    private static int s_reportedSoftwareFallback;

    /// <summary>
    /// Creates the best available encoder: each hardware encoder MFT is tried in merit order and
    /// has to pass a short self-test; the Microsoft software encoder is the fallback. With
    /// <see cref="EncoderPreference.Hardware"/> there is no fallback: throws if no hardware encoder works.
    /// </summary>
    public static H264Encoder Create(H264EncoderConfig config, EncoderPreference preference)
    {
        if (preference != EncoderPreference.Software)
        {
            List<(IMFActivate Activate, string Name)> candidates = EnumerateHardware();
            IMFActivate? chosen = null;
            try
            {
                foreach ((IMFActivate activate, string name) in candidates)
                {
                    if (TryCreateHardware(activate, name, config) is { } encoder)
                    {
                        chosen = activate;
                        return encoder;
                    }
                }
            }
            finally
            {
                // The chosen encoder owns its activation object; the other candidates are done with.
                foreach ((IMFActivate activate, _) in candidates)
                {
                    if (!ReferenceEquals(activate, chosen))
                    {
                        Mf.Release(activate);
                    }
                }
            }

            if (preference == EncoderPreference.Hardware)
            {
                throw new InvalidOperationException(candidates.Count == 0
                    ? "No hardware H.264 encoder is installed on this machine."
                    : "None of the hardware H.264 encoders on this machine could be used with these settings.");
            }

            if (Interlocked.Exchange(ref s_reportedSoftwareFallback, 1) == 0)
            {
                Log.Info(candidates.Count == 0
                    ? "No hardware H.264 encoder found; using the software encoder."
                    : "No usable hardware H.264 encoder; falling back to the software encoder.");
            }
        }

        return CreateSoftware(config);
    }

    public static H264Encoder CreateSoftware(H264EncoderConfig config) =>
        new((IMFTransform)new MSH264EncoderMFT(), null, SoftwareEncoderName, isHardware: false, config);

    /// <summary>Friendly names of the hardware H.264 encoder MFTs registered on this machine.</summary>
    public static IReadOnlyList<string> ListHardware()
    {
        List<(IMFActivate Activate, string Name)> candidates = EnumerateHardware();
        foreach ((IMFActivate activate, _) in candidates)
        {
            Mf.Release(activate);
        }

        return candidates.ConvertAll(c => c.Name);
    }

    private static H264Encoder? TryCreateHardware(IMFActivate activate, string name, H264EncoderConfig config)
    {
        try
        {
            // The self-test runs on a throwaway instance so the live stream starts from a clean encoder.
            // Both use the same activation object; only the encoder that is kept owns (and releases) it.
            using (var probe = new H264Encoder(Activate(activate), activate, name, isHardware: true, config, ownsActivate: false))
            {
                probe.SelfTest();
                probe.Close(drain: false);
            }

            return new H264Encoder(Activate(activate), activate, name, isHardware: true, config, ownsActivate: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Hardware encoder '{name}' cannot be used: {ex.Message}");
            activate.ShutdownObject();
            return null;
        }
    }

    private static IMFTransform Activate(IMFActivate activate)
    {
        Mf.Check(activate.ActivateObject(typeof(IMFTransform).GUID, out object transform), "Activate encoder MFT");
        return (IMFTransform)transform;
    }

    private static List<(IMFActivate Activate, string Name)> EnumerateHardware()
    {
        var result = new List<(IMFActivate, string)>();
        var outputType = new MFTRegisterTypeInfo { guidMajorType = MFMediaType.Video, guidSubtype = MFMediaType.H264 };

        HResult hr = MFExtern.MFTEnumEx(
            MFTransformCategory.MFT_CATEGORY_VIDEO_ENCODER,
            MFT_EnumFlag.Hardware | MFT_EnumFlag.SortAndFilter,
            null,
            outputType,
            out IMFActivate[] activates,
            out int count);

        if (Mf.Failed(hr) || activates is null)
        {
            return result;
        }

        for (int i = 0; i < count; i++)
        {
            string name = Mf.GetString(activates[i], MFAttributesClsid.MFT_FRIENDLY_NAME_Attribute) ?? $"Hardware H.264 encoder {i}";
            result.Add((activates[i], name));
        }

        return result;
    }
}
