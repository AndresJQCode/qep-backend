using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Modules.Integrations.UnitTests;

/// <summary>Todo lo que se registra, ya formateado y con la excepción entera: lo que guardaría el log
/// JSON de producción. Copia de <c>CapturedLogs</c> de 6612298.</summary>
internal sealed class UnitTestLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public string AllText => string.Join('\n', _entries);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
    }
}
