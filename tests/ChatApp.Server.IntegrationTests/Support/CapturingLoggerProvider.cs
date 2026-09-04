using Microsoft.Extensions.Logging;

namespace ChatApp.Server.IntegrationTests.Support;

/// <summary>线程安全地捕获内存日志事件，供审计 EventId/级别/字段断言使用。</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly List<CapturedLog> _entries = [];

    public IReadOnlyList<CapturedLog> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Add(CapturedLog entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Add(new CapturedLog(logLevel, eventId.Id, formatter(state, exception)));
    }
}

public sealed record CapturedLog(LogLevel Level, int EventId, string Message);
