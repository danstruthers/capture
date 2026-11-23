using Capture.Interop;
using Capture.Video;

namespace Capture.Tests;

/// <summary>
/// Screen capture on this machine's own screens: desktop duplication against GDI, composing the
/// desktop, and handing screens over between capturers. Each test is skipped, saying why, where
/// the machine lacks what it needs (an interactive desktop, desktop duplication, several screens).
/// </summary>
public class ScreenCaptureTests
{
    private const int Grid = 8;

    [FactNeeding(Needs.DesktopDuplication)]
    public void Desktop_duplication_and_gdi_capture_the_same_picture_of_the_primary_screen()
    {
        DisplayInfo primary = Machine.Displays.First(display => display.IsPrimary);

        CompareMethods(CaptureArea.ForScreen(primary));
    }

    [FactNeeding(Needs.DesktopDuplication | Needs.TwoScreens)]
    public void Desktop_duplication_and_gdi_compose_the_whole_desktop_the_same_way()
    {
        CompareMethods(CaptureArea.ForDesktop(Machine.Displays));
    }

    [FactNeeding(Needs.DesktopDuplication)]
    public void A_new_capturer_takes_over_desktop_duplication_from_the_one_it_replaces()
    {
        // A layout change builds the new streams while the old ones still hold the screens, and a
        // process can only duplicate a screen once at a time.
        CaptureArea area = CaptureArea.ForScreen(Machine.Displays.First(display => display.IsPrimary));
        ScreenCapturer old = new(area, drawCursor: false, CaptureMethod.Dxgi);

        ScreenCapturer replacement;
        using (old)
        {
            Assert.True(old.Capture(out _));
            replacement = new ScreenCapturer(area, drawCursor: false, CaptureMethod.Dxgi);
        }

        using (replacement)
        {
            Assert.True(replacement.Capture(out _));
            Assert.Equal("DXGI desktop duplication", replacement.Method);
        }
    }

    [FactNeeding(Needs.DesktopWithGaps)]
    public unsafe void Parts_of_the_desktop_no_screen_covers_stay_black()
    {
        CaptureArea desktop = CaptureArea.ForDesktop(Machine.Displays);
        (int x, int y)? uncovered = Machine.FindUncoveredPixel(desktop);

        using var capturer = new ScreenCapturer(desktop, drawCursor: true, CaptureMethod.Auto);
        Assert.True(capturer.Capture(out _));

        uint pixel = *(uint*)(capturer.Pixels + uncovered!.Value.y * capturer.Stride + uncovered.Value.x * 4);
        Assert.Equal(0u, pixel);
    }

    [FactNeeding(Needs.Screen)]
    public void Windows_reports_an_sdr_content_brightness_for_every_screen()
    {
        foreach (DisplayInfo display in Machine.Displays)
        {
            // The setting's range in Windows' display settings is 80 to 480 nits.
            double? whiteLevel = DisplayConfig.GetSdrWhiteLevel(display.DeviceName);
            Assert.NotNull(whiteLevel);
            Assert.InRange(whiteLevel.Value, 80, 480);
        }

        Assert.Null(DisplayConfig.GetSdrWhiteLevel(@"\\.\DISPLAY99"));
    }

    private static void CompareMethods(CaptureArea area)
    {
        using (var duplication = new ScreenCapturer(area, drawCursor: false, CaptureMethod.Dxgi))
        using (var gdi = new ScreenCapturer(area, drawCursor: false, CaptureMethod.Gdi))
        {
            Assert.Equal("DXGI desktop duplication", duplication.Method);
            Assert.Equal("GDI", gdi.Method);
            Assert.Equal((area.Width, area.Height), (duplication.Width, duplication.Height));

            // GDI's top-down BGRX copy is the reference. The desktop may change between the two grabs,
            // so compare coarse cell averages and allow a few attempts.
            double difference = double.MaxValue;
            for (int attempt = 0; attempt < 5 && difference > 6; attempt++)
            {
                Assert.True(duplication.Capture(out _));
                Assert.True(gdi.Capture(out _));
                difference = MeanDifference(CellAverages(duplication), CellAverages(gdi));
            }

            Assert.True(difference <= 6, $"The two capture methods differ by {difference:0.0} levels on average: the duplicated picture of {area} is flipped, shifted or has its colour channels in another order.");
        }
    }

    /// <summary>Average blue, green and red of each cell of a Grid x Grid division of the frame.</summary>
    private static unsafe double[] CellAverages(ScreenCapturer capturer)
    {
        double[] averages = new double[Grid * Grid * 3];
        int cellWidth = capturer.Width / Grid;
        int cellHeight = capturer.Height / Grid;
        byte* pixels = (byte*)capturer.Pixels;

        for (int cellY = 0; cellY < Grid; cellY++)
        {
            for (int cellX = 0; cellX < Grid; cellX++)
            {
                long blue = 0, green = 0, red = 0;
                int count = 0;

                // Every fourth pixel in both directions is plenty for an average.
                for (int y = cellY * cellHeight; y < (cellY + 1) * cellHeight; y += 4)
                {
                    byte* row = pixels + (long)y * capturer.Stride;
                    for (int x = cellX * cellWidth; x < (cellX + 1) * cellWidth; x += 4)
                    {
                        blue += row[x * 4];
                        green += row[x * 4 + 1];
                        red += row[x * 4 + 2];
                        count++;
                    }
                }

                int index = (cellY * Grid + cellX) * 3;
                averages[index] = (double)blue / count;
                averages[index + 1] = (double)green / count;
                averages[index + 2] = (double)red / count;
            }
        }

        return averages;
    }

    private static double MeanDifference(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
}
