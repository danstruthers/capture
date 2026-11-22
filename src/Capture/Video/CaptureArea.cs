namespace Capture.Video;

/// <summary>How the screens are turned into video streams.</summary>
internal enum ScreenLayout
{
    /// <summary>Each screen is its own stream.</summary>
    Separate,

    /// <summary>One stream showing all screens as they are arranged on the desktop.</summary>
    Desktop,
}

/// <summary>
/// What one video stream shows: a single screen, or several screens composed into one picture the
/// way they are arranged on the desktop. Parts of the desktop rectangle that no screen covers
/// (screens of different heights, for example) are black.
/// </summary>
internal sealed class CaptureArea
{
    public const string DesktopId = "desktop";

    private CaptureArea(string id, int number, string name, IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0)
        {
            throw new ArgumentException("A capture area needs at least one screen.", nameof(displays));
        }

        Id = id;
        Number = number;
        Name = name;
        Displays = displays;
        Left = displays.Min(d => d.Left);
        Top = displays.Min(d => d.Top);
        Width = displays.Max(d => d.Right) - Left;
        Height = displays.Max(d => d.Bottom) - Top;
    }

    /// <summary>"screen1", "screen2", ... or "desktop".</summary>
    public string Id { get; }

    /// <summary>The screen number, or 0 for the desktop. Identifies the stream in binary messages.</summary>
    public int Number { get; }

    public string Name { get; }

    public IReadOnlyList<DisplayInfo> Displays { get; }

    /// <summary>Position of the area on the virtual desktop.</summary>
    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public bool IsPrimary => Displays.Count == 1 && Displays[0].IsPrimary;

    public static CaptureArea ForScreen(DisplayInfo display) =>
        new($"screen{display.Number}", display.Number, $"Screen {display.Number}", [display]);

    public static CaptureArea ForDesktop(IReadOnlyList<DisplayInfo> displays) =>
        new(DesktopId, 0, "Desktop", displays);

    public static IReadOnlyList<CaptureArea> For(ScreenLayout layout, IReadOnlyList<DisplayInfo> displays) =>
        layout == ScreenLayout.Desktop ? [ForDesktop(displays)] : displays.Select(ForScreen).ToList();

    public override string ToString() => $"{Name} ({Width}x{Height} at {Left},{Top})";
}
