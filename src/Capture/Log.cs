namespace Capture;

/// <summary>Minimal thread-safe console logger.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static bool Verbose { get; set; }

    public static void Info(string message) => Write("info", message, null);

    public static void Warn(string message) => Write("warn", message, ConsoleColor.Yellow);

    public static void Error(string message) => Write("fail", message, ConsoleColor.Red);

    public static void Debug(string message)
    {
        if (Verbose)
        {
            Write("dbg ", message, ConsoleColor.DarkGray);
        }
    }

    private static void Write(string level, string message, ConsoleColor? color)
    {
        lock (Gate)
        {
            if (color is { } c)
            {
                Console.ForegroundColor = c;
            }

            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level}  {message}");

            if (color is not null)
            {
                Console.ResetColor();
            }
        }
    }
}
