using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Observability;

/// <summary>
/// Cuenta todo log <c>Error</c>/<c>Critical</c> de QEP (requests, <c>BackgroundService</c> y cualquier otro camino) sin tocar a quien
/// loguea. No escribe nada: la consola JSON sigue siendo la única salida.
/// <para>
/// Dos instrumentos: <c>qep.log.errors</c> (nivel, categoría y tipo de excepción; para paneles) y <c>qep.log.error_entries</c> (nivel y
/// si traía excepción; para la alerta). El segundo es un <see cref="ObservableCounter{T}"/> con sus cuatro series sembradas en cero:
/// <c>increase()</c> de Prometheus toma la primera muestra de una serie como base, así que una serie que nace al ocurrir el primer error
/// no dispararía su alerta. Ninguna etiqueta lleva el mensaje, la excepción ni el tenant.
/// </para>
/// <para>
/// No implementa <see cref="ISupportExternalScope"/> a propósito: no lee scopes, y así la redacción de rutas sensibles (que reenvuelve a
/// los proveedores que sí lo implementan) lo deja tal cual, en el orden que se registre.
/// </para>
/// </summary>
public sealed class ErrorCountingLoggerProvider : ILoggerProvider
{
    private const string NoException = "none";
    private const string IgnoredCategoryPrefix = "Microsoft.EntityFrameworkCore.";

    // Índice = (Critical ? 2 : 0) + (con excepción ? 1 : 0).
    private readonly long[] _entries = new long[4];
    private readonly Counter<long> _errors;
    private readonly ObservableCounter<long> _entriesCounter;

    public ErrorCountingLoggerProvider(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _errors = meter.CreateCounter<long>("qep.log.errors", unit: "{entry}", description: "Entradas de log Error o Critical.");
        _entriesCounter = meter.CreateObservableCounter(
            "qep.log.error_entries",
            ObserveEntries,
            unit: "{entry}",
            description: "Entradas de log Error o Critical por nivel y si traían excepción (series sembradas en cero).");
    }

    public ILogger CreateLogger(string categoryName) => new CountingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private IEnumerable<Measurement<long>> ObserveEntries()
    {
        yield return Entry("Error", false, 0);
        yield return Entry("Error", true, 1);
        yield return Entry("Critical", false, 2);
        yield return Entry("Critical", true, 3);
    }

    private Measurement<long> Entry(string level, bool hasException, int index) =>
        new(
            Interlocked.Read(ref _entries[index]),
            new KeyValuePair<string, object?>("level", level),
            new KeyValuePair<string, object?>("has_exception", hasException ? "true" : "false"));

    private void Count(LogLevel logLevel, string category, Exception? exception)
    {
        var level = logLevel == LogLevel.Critical ? "Critical" : "Error";
        var type = exception is null ? NoException : exception.GetType().FullName ?? exception.GetType().Name;
        Interlocked.Increment(ref _entries[(logLevel == LogLevel.Critical ? 2 : 0) + (exception is null ? 0 : 1)]);
        _errors.Add(
            1,
            new KeyValuePair<string, object?>("level", level),
            new KeyValuePair<string, object?>("category", category),
            new KeyValuePair<string, object?>("exception_type", type));
    }

    private sealed class CountingLogger(ErrorCountingLoggerProvider owner, string category) : ILogger
    {
        // EF registra en Error (con su excepción) los choques de índice único que el UnitOfWork ya traduce a un 409 de dominio:
        // contarlos dispararía la alerta de excepciones no controladas con un camino normal. Un error de base que de verdad se escapa
        // sale por el manejador de excepciones o por el worker que lo atrapa, y esos sí cuentan.
        private readonly bool _counted = !category.StartsWith(IgnoredCategoryPrefix, StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _counted && logLevel >= LogLevel.Error && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Contar nunca rompe a quien loguea ni se loguea a sí mismo.
            try
            {
                owner.Count(logLevel, category, exception);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
