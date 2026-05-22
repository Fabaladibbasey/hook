using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Hook.TestHelpers;

public sealed record LogEntry(LogLevel Level, string Formatted, Exception? Exception);

public sealed class RecordingLogger<T> : ILogger<T>
{
    // ConcurrentQueue avoids torn-write races when the SUT logs from concurrent
    // tasks (e.g. WolverineErrorPolicyTests cascade retries).
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    public IReadOnlyCollection<LogEntry> Entries => _entries;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel level,
        EventId id,
        TState state,
        Exception? ex,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new LogEntry(level, formatter(state, ex), ex));
}
