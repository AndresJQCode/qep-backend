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

    /// <summary>Spec 2026-11-22 §7: si se avisa a los líderes de un ticket que lleva horas hábiles sin responsable. Apagado por defecto.</summary>
    public bool UnassignedAlertEnabled { get; private set; }

    /// <summary>Spec 2026-11-22 §7: las horas hábiles sin responsable que dispara el aviso (1 a 72).</summary>
    public int UnassignedAlertHours { get; private set; }

    /// <summary>Spec 2026-11-22 §7: las membresías que reciben el aviso, en el orden en que se eligieron (a lo sumo 20). Es un arreglo y no
    /// una lista para que EF lo mapee como <c>uuid[]</c>; cada guardado asigna uno nuevo, así que EF detecta el cambio.</summary>
    public Guid[] UnassignedAlertRecipientIds { get; private set; } = [];

    /// <summary>Spec 2026-11-22 §7: el horario de atención elegido (de Mensajería, sin FK: es de otro módulo). Null = el predeterminado del
    /// tenant. Uno borrado se queda guardado y se lee como «borrado» hasta que alguien guarde otro.</summary>
    public Guid? UnassignedAlertScheduleId { get; private set; }

    /// <summary>Spec 2026-11-22 §7: cuándo se prendió el aviso por última vez. El reloj de un ticket arranca en el mayor entre
    /// <c>unassigned_since</c> y esto, así que prenderlo no dispara de golpe por tickets viejos. Apagar y volver a prender lo mueve.</summary>
    public DateTimeOffset? UnassignedAlertEnabledAt { get; private set; }

    /// <summary>El aviso como un solo valor, para leerlo y para pasarlo de vuelta a <see cref="Update"/>.</summary>
    public SupportUnassignedAlertSettings UnassignedAlert =>
        new(UnassignedAlertEnabled, UnassignedAlertHours, UnassignedAlertScheduleId, UnassignedAlertRecipientIds);

    /// <summary>Task-notices spec 2026-11-22 §5.4: the WhatsApp connection (of Integrations, no foreign key: another module's) the task
    /// notices leave through. Null = none chosen, and the notices go by email. One that was paused or lost its template stays stored
    /// until somebody saves another: the escalation falls back to email by itself.</summary>
    public Guid? TaskNoticeConnectionId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Los valores con que nace la fila: los mismos defaults de la columna.</summary>
    public static SupportTenantSettings CreateDefault(Guid tenantId, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            DueSoonBusinessDays = DefaultDueSoonBusinessDays,
            AutoCloseBusinessDays = DefaultAutoCloseBusinessDays,
            UnassignedAlertHours = SupportUnassignedAlertSettings.DefaultThresholdHours,
            UpdatedAt = now,
            Version = 1,
        };

    /// <summary>
    /// The whole row at once, as one PUT of the settings screen sends it (owner 2026-11-16): the version goes up once if anything
    /// changed, however many groups moved, and not at all otherwise. Every value is checked before any is written, so a rejected
    /// one leaves the row as it was. <paramref name="formalNextStep"/> is trimmed and an empty one is null; to keep the stored one,
    /// pass it back. <paramref name="unassignedAlert"/> (spec 2026-11-22 §7) travels whole, as the screen sends it; to keep the stored one,
    /// pass <see cref="UnassignedAlert"/> back. Turning it on from off stamps <see cref="UnassignedAlertEnabledAt"/>, so turning it off and
    /// on again moves it.
    /// </summary>
    public SupportTenantSettingsChanges Update(
        int dueSoonBusinessDays,
        string? formalNextStep,
        bool surveyEnabled,
        int autoCloseBusinessDays,
        SupportUnassignedAlertSettings unassignedAlert,
        Guid? taskNoticeConnectionId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(unassignedAlert);
        if (dueSoonBusinessDays is < MinDueSoonBusinessDays or > MaxDueSoonBusinessDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueSoonBusinessDays),
                dueSoonBusinessDays,
                $"The due-soon threshold is between {MinDueSoonBusinessDays} and {MaxDueSoonBusinessDays} business days.");
        }

        if (autoCloseBusinessDays is < MinAutoCloseBusinessDays or > MaxAutoCloseBusinessDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(autoCloseBusinessDays),
                autoCloseBusinessDays,
                $"The auto-close is between {MinAutoCloseBusinessDays} and {MaxAutoCloseBusinessDays} business days.");
        }

        unassignedAlert.EnsureValid();

        var normalizedNextStep = SupportFormalNextStep.Normalize(formalNextStep);
        var changes = new SupportTenantSettingsChanges(
            dueSoonBusinessDays != DueSoonBusinessDays,
            normalizedNextStep != FormalNextStep,
            surveyEnabled != SurveyEnabled,
            autoCloseBusinessDays != AutoCloseBusinessDays,
            !unassignedAlert.SameAs(UnassignedAlert),
            taskNoticeConnectionId != TaskNoticeConnectionId);
        if (!changes.Any)
        {
            return changes;
        }

        // Only a change from off to on moves the clock's floor: saving the same enabled alert with another threshold keeps it.
        if (unassignedAlert.Enabled && !UnassignedAlertEnabled)
        {
            UnassignedAlertEnabledAt = now;
        }

        UnassignedAlertEnabled = unassignedAlert.Enabled;
        UnassignedAlertHours = unassignedAlert.ThresholdHours;
        UnassignedAlertScheduleId = unassignedAlert.ScheduleId;
        UnassignedAlertRecipientIds = [.. unassignedAlert.RecipientIds];
        TaskNoticeConnectionId = taskNoticeConnectionId;

        DueSoonBusinessDays = dueSoonBusinessDays;
        FormalNextStep = normalizedNextStep;
        SurveyEnabled = surveyEnabled;
        AutoCloseBusinessDays = autoCloseBusinessDays;
        UpdatedAt = now;
        Version++;
        return changes;
    }

    public void ChangeDueSoon(int businessDays, DateTimeOffset now) =>
        Update(businessDays, FormalNextStep, SurveyEnabled, AutoCloseBusinessDays, UnassignedAlert, TaskNoticeConnectionId, now);

    /// <summary>Fase 4b (H14): la encuesta y el cierre automático. Con los mismos valores no cambia nada ni sube la versión.</summary>
    public void ChangeSurvey(bool enabled, int autoCloseBusinessDays, DateTimeOffset now) =>
        Update(DueSoonBusinessDays, FormalNextStep, enabled, autoCloseBusinessDays, UnassignedAlert, TaskNoticeConnectionId, now);

    /// <summary>Recortado; vacío es null. Con el mismo valor no cambia nada ni sube la versión.</summary>
    public void ChangeFormalNextStep(string? text, DateTimeOffset now) =>
        Update(DueSoonBusinessDays, text, SurveyEnabled, AutoCloseBusinessDays, UnassignedAlert, TaskNoticeConnectionId, now);
}

/// <summary>Which groups of <see cref="SupportTenantSettings"/> an <see cref="SupportTenantSettings.Update"/> changed; the handler
/// names them in the audit row.</summary>
public readonly record struct SupportTenantSettingsChanges(
    bool DueSoonBusinessDays, bool FormalNextStep, bool SurveyEnabled, bool AutoCloseBusinessDays, bool UnassignedAlert, bool TaskNotice)
{
    public bool Any => DueSoonBusinessDays || FormalNextStep || SurveyEnabled || AutoCloseBusinessDays || UnassignedAlert || TaskNotice;
}
