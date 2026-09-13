using Microsoft.Extensions.Logging;

namespace Modules.Quotations.IntegrationTests;

internal sealed record RecordedLogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> State, string Message);

/// <summary>Un ILogger que guarda cada entrada con sus valores estructurados, para afirmar qué se
/// loguea sin depender del formato del mensaje.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<RecordedLogEntry> _entries = [];

    public IReadOnlyList<RecordedLogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        _entries.Add(new RecordedLogEntry(
            logLevel,
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            formatter(state, exception)));
    }
}
