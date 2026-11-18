using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Modules.Ai.IntegrationTests;

/// <summary>Captura cada línea de log del host (con sus valores y scopes) para las pruebas de no-filtración de los dos proveedores.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lines.Enqueue($"{category} scope {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
            lines.Enqueue($"{category} {formatter(state, exception)} {values} {exception}");
        }
    }
}
