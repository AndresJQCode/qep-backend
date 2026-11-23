namespace Modules.Scheduling.Domain;

/// <summary>
/// Las reglas de reserva de un tenant: una fila por tenant, con su <c>version</c>. La aprovisiona el primer acceso con
/// <see cref="SchedulingDefaults"/>. El recordatorio sólo puede prenderse con su conexión y su plantilla; apagado, las suelta.
/// </summary>
public sealed class SchedulingSettings : VersionedEntity
{
    private SchedulingSettings()
    {
    }

    public ConfirmationMode ConfirmationMode { get; private set; }

    public int MinNoticeMinutes { get; private set; }

    public int HorizonDays { get; private set; }

    public int ClientCancelCutoffHours { get; private set; }

    public bool ReminderEnabled { get; private set; }

    public int ReminderOffsetHours { get; private set; }

    public Guid? ReminderConnectionId { get; private set; }

    public Guid? ReminderTemplateId { get; private set; }

    public static SchedulingSettings Create(Guid tenantId, DateTimeOffset now)
    {
        var settings = new SchedulingSettings
        {
            ConfirmationMode = SchedulingDefaults.ConfirmationMode,
            MinNoticeMinutes = SchedulingDefaults.MinNoticeMinutes,
            HorizonDays = SchedulingDefaults.HorizonDays,
            ClientCancelCutoffHours = SchedulingDefaults.ClientCancelCutoffHours,
            ReminderEnabled = SchedulingDefaults.ReminderEnabled,
            ReminderOffsetHours = SchedulingDefaults.ReminderOffsetHours,
        };
        settings.Initialize(tenantId, now);
        return settings;
    }

    public void Update(
        ConfirmationMode confirmationMode,
        int minNoticeMinutes,
        int horizonDays,
        int clientCancelCutoffHours,
        bool reminderEnabled,
        int reminderOffsetHours,
        Guid? reminderConnectionId,
        Guid? reminderTemplateId,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minNoticeMinutes, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minNoticeMinutes, 10_080);
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(horizonDays, 365);
        ArgumentOutOfRangeException.ThrowIfLessThan(clientCancelCutoffHours, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clientCancelCutoffHours, 720);
        ArgumentOutOfRangeException.ThrowIfLessThan(reminderOffsetHours, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reminderOffsetHours, 168);

        if (reminderEnabled && reminderConnectionId is null)
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.ReminderConnectionInvalid, "The reminder needs the WhatsApp number it is sent from.");
        }

        if (reminderEnabled && reminderTemplateId is null)
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.ReminderTemplateMissing, "The reminder needs its approved template.");
        }

        ConfirmationMode = confirmationMode;
        MinNoticeMinutes = minNoticeMinutes;
        HorizonDays = horizonDays;
        ClientCancelCutoffHours = clientCancelCutoffHours;
        ReminderEnabled = reminderEnabled;
        ReminderOffsetHours = reminderOffsetHours;
        ReminderConnectionId = reminderEnabled ? reminderConnectionId : null;
        ReminderTemplateId = reminderEnabled ? reminderTemplateId : null;
        Touch(now);
    }
}
