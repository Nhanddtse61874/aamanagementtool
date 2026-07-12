using Microsoft.Extensions.Logging;

namespace TimesheetApp.ApiTests;

/// <summary>Captures the host's log output into a list, so a test can assert on something that is
/// DELIBERATELY not persisted anywhere else — the one-time generated admin password.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<string> _sink;

    public ListLoggerProvider(List<string> sink) => _sink = sink;

    public ILogger CreateLogger(string categoryName) => new ListLogger(_sink);

    public void Dispose() { }

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _sink;

        public ListLogger(List<string> sink) => _sink = sink;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_sink)
            {
                _sink.Add(message);
            }
        }
    }
}
