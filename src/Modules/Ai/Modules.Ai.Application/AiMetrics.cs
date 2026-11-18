using System.Diagnostics.Metrics;

namespace Modules.Ai.Application;

/// <summary>
/// Plan 2026-11-12, Tarea 7: las llamadas al modelo y sus errores, etiquetados por <c>provider</c> para que el panel separe Anthropic de
/// OpenAI (C-19: el saldo o la clave de una cuenta no dicen nada de la otra). Meter <c>Qep.Ai</c>: sale por OTLP con el comodín
/// <c>Qep.*</c>. Sin tenant ni dato personal en las etiquetas: sólo el proveedor, el resultado y el código cerrado del error.
/// </summary>
public sealed class AiMetrics
{
    public const string MeterName = "Qep.Ai";

    private readonly Counter<long> _calls;
    private readonly Counter<long> _errors;

    public AiMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _calls = meter.CreateCounter<long>("qep.ai.model.calls", description: "Llamadas al modelo, por proveedor y resultado.");
        _errors = meter.CreateCounter<long>("qep.ai.model.errors", description: "Errores del modelo o del proveedor, por proveedor y código.");
    }

    /// <param name="outcome">Uno de <see cref="AiRunOutcomes"/>.</param>
    public void RecordCall(string provider, string outcome) =>
        _calls.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", outcome));

    /// <param name="code">Un código cerrado de <see cref="AiModelErrorCodes"/>.</param>
    public void RecordError(string provider, string code) =>
        _errors.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("code", code));
}
