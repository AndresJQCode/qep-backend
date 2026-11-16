namespace Modules.Support.Domain;

/// <summary>
/// La fila de soporte de cada tenant (plan D1 y D15). La crea <c>ISupportProvisioner</c> en el primer acceso, con SQL crudo, y
/// guarda también el contador del radicado (<c>next_ticket_number</c>), que este tipo no mapea: el <c>UPDATE</c> del contador no
/// toca <see cref="Version"/>, así que guardar el umbral nunca choca con un alta.
/// </summary>
public sealed class SupportTenantSettings
{
    public const int DefaultDueSoonBusinessDays = 3;

    public const int MinDueSoonBusinessDays = 0;

    public const int MaxDueSoonBusinessDays = 30;

    /// <summary>Fase 4b (H6): el «siguiente paso» de la respuesta formal, en texto plano.</summary>
    public const int FormalNextStepMaxLength = SupportFormalNextStep.MaxLength;

    /// <summary>Fase 4b (H14, owner 2026-11-16, Q-19): los días hábiles sin respuesta del cliente para cerrar solo un ticket resuelto.
    /// El cierre automático nunca se apaga.</summary>
    public const int DefaultAutoCloseBusinessDays = 5;

    public const int MinAutoCloseBusinessDays = 1;

    public const int MaxAutoCloseBusinessDays = 30;

    // EF Core materializa por acá.
    private SupportTenantSettings()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>Cuántos días hábiles antes del vencimiento un ticket está «por vencer» [owner 2026-11-14]; 0 lo apaga.</summary>
    public int DueSoonBusinessDays { get; private set; }

    /// <summary>Fase 4b (H6): la sección 7 del PDF de la respuesta formal cuando el tipo del ticket no trae la suya. Null: sin texto,
    /// y si el tipo tampoco tiene, el PDF omite la sección.</summary>
    public string? FormalNextStep { get; private set; }

    /// <summary>Fase 4b (H14, owner 2026-11-16, Q-20): si se manda la encuesta de satisfacción al resolver. Apagada por defecto; apagada,
    /// la encuesta igual se pide y el worker la salta con <c>survey_disabled</c>.</summary>
    public bool SurveyEnabled { get; private set; }

    /// <summary>Fase 4b (H14, H22): los días hábiles sin respuesta del cliente para cerrar solo un ticket resuelto (1 a 30).</summary>
    public int AutoCloseBusinessDays { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Los valores con que nace la fila: los mismos defaults de la columna.</summary>
    public static SupportTenantSettings CreateDefault(Guid tenantId, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            DueSoonBusinessDays = DefaultDueSoonBusinessDays,
            AutoCloseBusinessDays = DefaultAutoCloseBusinessDays,
            UpdatedAt = now,
            Version = 1,
        };

    public void ChangeDueSoon(int businessDays, DateTimeOffset now)
    {
        if (businessDays is < MinDueSoonBusinessDays or > MaxDueSoonBusinessDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(businessDays),
                businessDays,
                $"The due-soon threshold is between {MinDueSoonBusinessDays} and {MaxDueSoonBusinessDays} business days.");
        }

        if (businessDays == DueSoonBusinessDays)
        {
            return;
        }

        DueSoonBusinessDays = businessDays;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Fase 4b (H14): la encuesta y el cierre automático. Con los mismos valores no cambia nada ni sube la versión.</summary>
    public void ChangeSurvey(bool enabled, int autoCloseBusinessDays, DateTimeOffset now)
    {
        if (autoCloseBusinessDays is < MinAutoCloseBusinessDays or > MaxAutoCloseBusinessDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(autoCloseBusinessDays),
                autoCloseBusinessDays,
                $"The auto-close is between {MinAutoCloseBusinessDays} and {MaxAutoCloseBusinessDays} business days.");
        }

        if (enabled == SurveyEnabled && autoCloseBusinessDays == AutoCloseBusinessDays)
        {
            return;
        }

        SurveyEnabled = enabled;
        AutoCloseBusinessDays = autoCloseBusinessDays;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Recortado; vacío es null. Con el mismo valor no cambia nada ni sube la versión.</summary>
    public void ChangeFormalNextStep(string? text, DateTimeOffset now)
    {
        var normalized = SupportFormalNextStep.Normalize(text);
        if (normalized == FormalNextStep)
        {
            return;
        }

        FormalNextStep = normalized;
        UpdatedAt = now;
        Version++;
    }
}
