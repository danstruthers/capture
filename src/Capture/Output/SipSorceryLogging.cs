using Microsoft.Extensions.Logging;

namespace Capture.Output;

/// <summary>
/// Routes the WebRTC library's log output to the console: errors always, everything down to
/// debug level when the application runs with --verbose.
/// </summary>
internal sealed class SipSorceryLogging : ILoggerFactory
{
    public static void Install() => SIPSorcery.LogFactory.Set(new SipSorceryLogging());

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Adapter(categoryName);

    public void Dispose()
    {
    }

    private sealed class Adapter : ILogger
    {
        private readonly string _category;

        public Adapter(string category)
        {
            int lastDot = category.LastIndexOf('.');
            _category = lastDot >= 0 ? category[(lastDot + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= (global::Capture.Log.Verbose ? LogLevel.Debug : LogLevel.Error);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = $"[webrtc/{_category}] {formatter(state, exception)}";
            if (logLevel >= LogLevel.Error)
            {
                global::Capture.Log.Error(message);
            }
            else if (logLevel == LogLevel.Warning)
            {
                global::Capture.Log.Warn(message);
            }
            else
            {
                global::Capture.Log.Debug(message);
            }
        }
    }
}
