using Capture.Output;

namespace Capture.Tests;

/// <summary>What recording files are called; the disk is a set of names here.</summary>
public sealed class RecordingNamesTests
{
    private static readonly string Folder = Path.GetFullPath(@"C:\recordings");
    private static readonly DateTime Now = new(2026, 11, 23, 9, 5, 7);

    private static string In(string name) => Path.Combine(Folder, name);

    [Fact]
    public void A_streams_files_are_numbered_by_segment_or_by_part()
    {
        var plain = new RecordingNames(Folder, "take.mp4", segmented: false, overwrite: false);
        var segmented = new RecordingNames(Folder, "take.mp4", segmented: true, overwrite: false);

        Assert.Equal([In("take.mp4"), In("take-part2.mp4"), In("take-part3.mp4")], [.. Enumerable.Range(1, 3).Select(n => plain.ForFile(In("take.mp4"), n))]);
        Assert.Equal([In("take-001.mp4"), In("take-002.mp4")], [.. Enumerable.Range(1, 2).Select(n => segmented.ForFile(In("take.mp4"), n))]);
    }

    [Fact]
    public void With_several_streams_each_files_name_has_the_streams_id()
    {
        Assert.Equal(In("take.mp4"), RecordingNames.ForStream(In("take.mp4"), "screen1", streamCount: 1));
        Assert.Equal(In("take-screen2.mp4"), RecordingNames.ForStream(In("take.mp4"), "screen2", streamCount: 2));
    }

    [Fact]
    public void Later_recordings_are_numbered_past_the_names_already_taken()
    {
        var names = new RecordingNames(Folder, "take.mp4", segmented: false, overwrite: false);
        HashSet<string> disk = [In("take-2.mp4")];

        Assert.Equal(In("take.mp4"), names.ForRecording(1, ["screen1"], Now, disk.Contains));
        Assert.Equal(In("take-3.mp4"), names.ForRecording(2, ["screen1"], Now, disk.Contains));
    }

    [Fact]
    public void A_name_is_taken_if_the_first_file_of_any_stream_exists()
    {
        var names = new RecordingNames(Folder, "take.mp4", segmented: true, overwrite: false);
        HashSet<string> disk = [In("take-2-screen2-001.mp4")];

        Assert.Equal(In("take-3.mp4"), names.ForRecording(2, ["screen1", "screen2"], Now, disk.Contains));
    }

    [Fact]
    public void A_taken_output_name_is_refused_unless_overwriting()
    {
        HashSet<string> disk = [In("take.mp4")];

        var careful = new RecordingNames(Folder, "take.mp4", segmented: false, overwrite: false);
        Assert.Contains("--overwrite", Assert.Throws<RefusedException>(() => careful.ForRecording(1, ["screen1"], Now, disk.Contains)).Message);

        var overwriting = new RecordingNames(Folder, "take.mp4", segmented: false, overwrite: true);
        Assert.Equal(In("take.mp4"), overwriting.ForRecording(1, ["screen1"], Now, disk.Contains));
    }

    [Fact]
    public void Without_an_output_name_recordings_are_named_after_when_they_started()
    {
        var names = new RecordingNames(Folder, null, segmented: false, overwrite: false);
        HashSet<string> disk = [In("capture-20261123-090507.mp4")];

        Assert.Equal(In("capture-20261123-090507-2.mp4"), names.ForRecording(1, ["screen1"], Now, disk.Contains));
        Assert.Equal(Folder, names.Folder);
    }
}
