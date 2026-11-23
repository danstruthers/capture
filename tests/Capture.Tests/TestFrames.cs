using Capture.Interop;
using Capture.Video;
using MediaFoundation;

namespace Capture.Tests;

/// <summary>Synthetic screen frames: four coloured quadrants and a block that moves so every frame differs.</summary>
internal static unsafe class TestFrames
{
    public const int Width = 640;
    public const int Height = 360;
    public const int Fps = 15;

    public static readonly H264EncoderConfig Config = new(Width, Height, Fps, BitrateBps: 1_000_000, GopFrames: Fps * 2, H264Profile.Baseline);

    /// <summary>Top-left red, top-right green, bottom-left blue, bottom-right white; bytes are B, G, R, X.</summary>
    public static IMFSample Create(int index)
    {
        byte[] pixels = new byte[Width * Height * 4];
        fixed (byte* start = pixels)
        {
            uint* pixel = (uint*)start;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    bool left = x < Width / 2;
                    bool top = y < Height / 2;
                    *pixel++ = (top, left) switch
                    {
                        (true, true) => 0x00FF0000u,   // red
                        (true, false) => 0x0000FF00u,  // green
                        (false, true) => 0x000000FFu,  // blue
                        _ => 0x00FFFFFFu,              // white
                    };
                }
            }

            // A grey block that travels along the horizontal centre line.
            int blockX = 40 + index * 12 % (Width - 120);
            for (int y = Height / 2 - 20; y < Height / 2 + 20; y++)
            {
                for (int x = blockX; x < blockX + 40; x++)
                {
                    ((uint*)start)[y * Width + x] = 0x00808080u;
                }
            }
        }

        long duration = MediaClock.TicksPerSecond / Fps;
        return Mf.CreateSample(pixels, index * duration, duration);
    }

    public static long TimeOf(int index) => index * (MediaClock.TicksPerSecond / Fps);
}
