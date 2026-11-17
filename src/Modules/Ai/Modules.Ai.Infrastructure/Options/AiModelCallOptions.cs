using Microsoft.Extensions.Options;
using Modules.Ai.Application;

namespace Modules.Ai.Infrastructure.Options;

/// <summary>
/// <c>Ai:Model</c>: los topes de D-A20 y del contexto (spec 2026-10-29 §6.3). Los números son la propuesta del spec y siguen abiertos
/// (P-2): viven en configuración para ajustarlos sin código. <see cref="MaxContextTokens"/> no tiene valor propuesto en ningún documento,
/// así que sin configurarlo sólo rige el tope de turnos.
/// </summary>
public sealed class AiModelCallOptions
{
    public const string SectionName = "Ai:Model";

    /// <summary>C-13: el tope de salida de cada llamada. Sin streaming, 16.000 cabe en el timeout.</summary>
    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>D-A20 y C-13: el timeout de una llamada. El SDK no reintenta (<c>MaxRetries = 0</c>), así que es el tiempo real.</summary>
    public int CallTimeoutSeconds { get; set; } = 45;

    /// <summary>D-A20: el tope de todo un trabajo, sumando llamadas y herramientas.</summary>
    public int JobTimeoutSeconds { get; set; } = 120;

    /// <summary>D-A20: llamadas al modelo por trabajo.</summary>
    public int MaxModelCalls { get; set; } = 6;

    /// <summary>D-A20: llamadas a herramientas por trabajo.</summary>
    public int MaxToolCalls { get; set; } = 8;

    /// <summary>§6.3: los últimos turnos de la conversación que entran al contexto.</summary>
    public int MaxHistoryTurns { get; set; } = 30;

    /// <summary>§6.3: el tope del historial en tokens, estimado por caracteres. <c>null</c>: sin tope por tokens (P-2).</summary>
    public int? MaxContextTokens { get; set; }

    public AiRunLimits ToLimits() =>
        new(
            MaxOutputTokens,
            TimeSpan.FromSeconds(JobTimeoutSeconds),
            MaxModelCalls,
            MaxToolCalls,
            MaxHistoryTurns,
            MaxContextTokens);
}

/// <summary>Todos positivos: un cero dejaría al agente sin poder responder nunca, y eso tiene que frenar el arranque.</summary>
internal sealed class AiModelCallOptionsValidator : IValidateOptions<AiModelCallOptions>
{
    public ValidateOptionsResult Validate(string? name, AiModelCallOptions options)
    {
        var failures = new List<string>();
        Positive(options.MaxOutputTokens, nameof(options.MaxOutputTokens), failures);
        Positive(options.CallTimeoutSeconds, nameof(options.CallTimeoutSeconds), failures);
        Positive(options.JobTimeoutSeconds, nameof(options.JobTimeoutSeconds), failures);
        Positive(options.MaxModelCalls, nameof(options.MaxModelCalls), failures);
        Positive(options.MaxToolCalls, nameof(options.MaxToolCalls), failures);
        Positive(options.MaxHistoryTurns, nameof(options.MaxHistoryTurns), failures);
        if (options.MaxContextTokens is { } tokens)
        {
            Positive(tokens, nameof(options.MaxContextTokens), failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Positive(int value, string key, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{AiModelCallOptions.SectionName}:{key} must be greater than zero.");
        }
    }
}
