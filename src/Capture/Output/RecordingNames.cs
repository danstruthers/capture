namespace Capture.Output;

/// <summary>
/// What recording files are called. A recording has one file per video stream at a time, and
/// those can run to several files each (segments, or a change of encoder configuration):
/// <list type="bullet">
/// <item>a recording: take.mp4 (--output), then take-2.mp4, take-3.mp4, ... for the next ones;
/// without --output, capture-&lt;date&gt;-&lt;time&gt;.mp4;</item>
/// <item>a stream's file, with several streams: take-screen1.mp4, take-screen2.mp4;</item>
/// <item>a stream's n-th file: take-001.mp4, take-002.mp4, ... with a segment length; otherwise
/// take.mp4, then take-part2.mp4, ... if the encoder configuration changes.</item>
/// </list>
/// Only <see cref="ForRecording"/> looks at the disk, through the function it is given.
/// </summary>
/// <param name="directory">The folder recordings go to.</param>
/// <param name="output">The recording's name (relative to <paramref name="directory"/> unless it is a full path), or null for timestamped names.</param>
internal sealed class RecordingNames(string directory, string? output, bool segmented, bool overwrite)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly string? _output = output is null ? null : Path.GetFullPath(output, Path.GetFullPath(directory));

    /// <summary>The folder the recording files are written to.</summary>
    public string Folder => _output is null ? _directory : Path.GetDirectoryName(_output)!;

    /// <summary>The name of a stream's files within a recording: the recording's own, plus the stream's id when there are several streams.</summary>
    public static string ForStream(string recording, string streamId, int streamCount) =>
        streamCount <= 1 ? recording : $"{Stem(recording)}-{streamId}{Path.GetExtension(recording)}";

    /// <summary>The <paramref name="number"/>-th file of a stream, counting from 1.</summary>
    public string ForFile(string stream, int number) =>
        segmented ? $"{Stem(stream)}-{number:000}{Path.GetExtension(stream)}"
        : number == 1 ? stream
        : $"{Stem(stream)}-part{number}{Path.GetExtension(stream)}";

    /// <summary>
    /// The name of the <paramref name="number"/>-th recording (counting from 1) of the given
    /// streams. A name is taken if the first file of any of its streams <paramref name="exists"/>;
    /// a taken name is passed over, except that with --output (and without --overwrite) the first
    /// recording is refused instead, so that nobody's file is replaced by surprise.
    /// </summary>
    public string ForRecording(int number, IReadOnlyList<string> streamIds, DateTime now, Func<string, bool> exists)
    {
        bool Taken(string recording) =>
            streamIds.Any(id => exists(ForFile(ForStream(recording, id, streamIds.Count), 1)));

        if (_output is null)
        {
            string stem = Path.Combine(_directory, $"capture-{now:yyyyMMdd-HHmmss}");
            string path = $"{stem}.mp4";
            for (int n = 2; Taken(path); n++)
            {
                path = $"{stem}-{n}.mp4";
            }

            return path;
        }

        if (number == 1)
        {
            if (!overwrite && Taken(_output))
            {
                throw new RefusedException($"A recording called {Path.GetFileName(_output)} already exists in {Path.GetDirectoryName(_output)}. Choose another --output name or pass --overwrite.");
            }

            return _output;
        }

        for (int n = number; ; n++)
        {
            string path = $"{Stem(_output)}-{n}{Path.GetExtension(_output)}";
            if (overwrite || !Taken(path))
            {
                return path;
            }
        }
    }

    private static string Stem(string path) => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
}
