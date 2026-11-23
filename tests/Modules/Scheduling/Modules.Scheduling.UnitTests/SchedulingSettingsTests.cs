using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21 (Agenda), «Aprovisionamiento» y «Reglas de reserva»: los valores por defecto viven en SchedulingDefaults.
public sealed class SchedulingSettingsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewTenantStartsWithTheDefaults()
    {
        var settings = SchedulingSettings.Create(Tenant, Now);

        Assert.Equal(
            (ConfirmationMode.Automatic, 120, 30, 24, false, 24),
            (settings.ConfirmationMode, settings.MinNoticeMinutes, settings.HorizonDays, settings.ClientCancelCutoffHours,
                settings.ReminderEnabled, settings.ReminderOffsetHours));
        Assert.Equal(1, settings.Version);
        Assert.Null(settings.ReminderConnectionId);
    }

    [Fact]
    public void TheDefaultsAreTheOnesTheSpecFixes()
    {
        Assert.Equal(120, SchedulingDefaults.MinNoticeMinutes);
        Assert.Equal(30, SchedulingDefaults.HorizonDays);
        Assert.Equal(24, SchedulingDefaults.ClientCancelCutoffHours);
        Assert.Equal(24, SchedulingDefaults.ReminderOffsetHours);
        Assert.False(SchedulingDefaults.ReminderEnabled);
        Assert.Equal(ConfirmationMode.Automatic, SchedulingDefaults.ConfirmationMode);
    }

    [Fact]
    public void UpdatingChangesTheRulesAndBumpsTheVersion()
    {
        var settings = SchedulingSettings.Create(Tenant, Now);
        var connection = Guid.CreateVersion7();

        settings.Update(ConfirmationMode.Manual, 60, 90, 12, reminderEnabled: true, 48, connection, null, Now.AddMinutes(1));

        Assert.Equal((ConfirmationMode.Manual, 60, 90, 12, true, 48), (settings.ConfirmationMode, settings.MinNoticeMinutes,
            settings.HorizonDays, settings.ClientCancelCutoffHours, settings.ReminderEnabled, settings.ReminderOffsetHours));
        Assert.Equal((connection, 2), (settings.ReminderConnectionId, (int)settings.Version));
    }

    [Theory]
    [InlineData(-1, 30, 24, 24)]
    [InlineData(120, 0, 24, 24)]
    [InlineData(120, 366, 24, 24)]
    [InlineData(120, 30, -1, 24)]
    [InlineData(120, 30, 24, 0)]
    [InlineData(120, 30, 24, 169)]
    public void ARuleOutsideItsRangeIsRejected(int minNotice, int horizon, int cutoff, int reminderOffset)
    {
        var settings = SchedulingSettings.Create(Tenant, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            settings.Update(ConfirmationMode.Automatic, minNotice, horizon, cutoff, false, reminderOffset, null, null, Now));
    }

    [Fact]
    public void ARemindingTenantNeedsOnlyItsConnection()
    {
        var settings = SchedulingSettings.Create(Tenant, Now);

        var noConnection = Assert.Throws<SchedulingDomainException>(() =>
            settings.Update(ConfirmationMode.Automatic, 120, 30, 24, true, 24, null, null, Now));

        Assert.Equal(SchedulingErrorCodes.ReminderConnectionInvalid, noConnection.Code);
        Assert.Equal(1, settings.Version);

        var connection = Guid.CreateVersion7();
        settings.Update(ConfirmationMode.Automatic, 120, 30, 24, true, 24, connection, null, Now);

        Assert.True(settings.ReminderEnabled);
        Assert.Equal(connection, settings.ReminderConnectionId);
    }

    [Fact]
    public void TurningTheReminderOffDropsTheConnection()
    {
        var settings = SchedulingSettings.Create(Tenant, Now);
        settings.Update(ConfirmationMode.Automatic, 120, 30, 24, true, 24, Guid.CreateVersion7(), null, Now);

        settings.Update(ConfirmationMode.Automatic, 120, 30, 24, false, 24, Guid.CreateVersion7(), null, Now.AddMinutes(1));

        Assert.False(settings.ReminderEnabled);
        Assert.Null(settings.ReminderConnectionId);
    }

    // D-18: el remitente por defecto del recordatorio se conserva aunque el recordatorio esté apagado.
    [Fact]
    public void TheReminderSenderIsKeptWithTheReminderOff()
    {
        var settings = SchedulingSettings.Create(Tenant, Now);
        var sender = Guid.CreateVersion7();

        settings.Update(ConfirmationMode.Manual, 60, 30, 24, reminderEnabled: false, 24, null, sender, Now.AddMinutes(1));

        Assert.Equal(sender, settings.ReminderSenderMemberId);
        Assert.Null(settings.ReminderConnectionId);
    }
}
