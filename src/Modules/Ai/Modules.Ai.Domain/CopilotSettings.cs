namespace Modules.Ai.Domain;

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C1 y §5.1: la configuración del copiloto, una por tenant (<c>ai.copilot_settings</c>). No es un
/// <see cref="Agent"/>: no tiene conexiones, disparador ni principal de servicio. Sin fila, vale <see cref="Unconfigured"/> con la versión
/// 0: el primer guardado la crea con <c>If-Match: "0"</c>.
/// </summary>
public sealed class CopilotSettings
{
    /// <summary>§5.1: el glosario y el contexto del negocio que el tenant le da al copiloto.</summary>
    public const int InstructionsMaxLength = 4000;

    /// <summary>OC-3 (default del plan): 90 días, entre 7 y 365.</summary>
    public const int DefaultRetentionDays = 90;

    public const int MinRetentionDays = 7;

    public const int MaxRetentionDays = 365;

    private CopilotSettings()
    {
    }

    public Guid TenantId { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>La clave del proveedor (<see cref="AiProviders"/>); <c>null</c> hasta configurarlo.</summary>
    public string? Provider { get; private set; }

    public string? Model { get; private set; }

    public string Instructions { get; private set; } = string.Empty;

    /// <summary>D-C11: tope mensual de tokens (entrada más salida) del tenant; <c>null</c> es sin tope.</summary>
    public long? MonthlyTokenBudget { get; private set; }

    public int RetentionDays { get; private set; } = DefaultRetentionDays;

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public long Version { get; private set; }

    /// <summary>Tiene proveedor y modelo elegidos, aunque esté apagado.</summary>
    public bool IsConfigured => Provider is not null && Model is not null;

    public static CopilotSettings Unconfigured(Guid tenantId) => new() { TenantId = tenantId };

    /// <summary>Reemplaza todo lo editable. Las reglas que el formulario marca por campo las da el validador de la aplicación; acá quedan
    /// las del <c>CHECK</c> (prendido exige proveedor y modelo) y los rangos, como último candado.</summary>
    public void Update(
        bool enabled, string? provider, string? model, string instructions, long? monthlyTokenBudget, int retentionDays, Guid by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        var trimmed = instructions.Trim();
        if (trimmed.Length > InstructionsMaxLength)
        {
            throw new ArgumentException($"Instructions are at most {InstructionsMaxLength} characters.", nameof(instructions));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, MinRetentionDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retentionDays, MaxRetentionDays);
        if (monthlyTokenBudget is { } budget)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget, nameof(monthlyTokenBudget));
        }

        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? null : provider;
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model;
        if (enabled && (normalizedProvider is null || normalizedModel is null))
        {
            throw new AiDomainException(
                AiErrorCodes.CopilotModelNotAvailable,
                "Elige el proveedor y el modelo antes de prender el copiloto.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["model"] = [AiErrorCodes.CopilotModelNotAvailable] });
        }

        Enabled = enabled;
        Provider = normalizedProvider;
        Model = normalizedModel;
        Instructions = trimmed;
        MonthlyTokenBudget = monthlyTokenBudget;
        RetentionDays = retentionDays;
        UpdatedBy = by;
        UpdatedAt = now;
        Version++;
    }
}
