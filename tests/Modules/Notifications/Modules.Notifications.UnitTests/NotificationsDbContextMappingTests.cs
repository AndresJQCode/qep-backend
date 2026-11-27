using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// El inbox con reclamo (spec 2026-09-13, Sección 1), contra el modelo de EF y no contra una base.
/// El reclamo es SQL crudo (InboxClaims) que nombra las columnas a mano: un nombre que EF pusiera por
/// convención rompería el reclamo sin que el compilador lo vea. Mismo criterio que
/// QuotationsDbContextMappingTests.
/// </summary>
public sealed class NotificationsDbContextMappingTests
{
    [Fact]
    public void TheInboxCarriesTheClaimColumns()
    {
        // El factory de diseño arma el contexto sin abrir conexión: construir el modelo no la necesita.
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var inbox = model.FindEntityType(typeof(NotificationInboxMessage));

        Assert.NotNull(inbox);
        Assert.Equal("inbox_messages", inbox.GetTableName());
        Assert.Equal("notifications", inbox.GetSchema());
        Assert.Equal(
            ["attempts", "claimed_until", "consumer", "message_id", "processed_at"],
            inbox.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
    }

    // processed_at en null es "reclamado y sin terminar". attempts nace en 1 para que las filas que ya
    // existen —todas procesadas— queden con un intento sin que la migración tenga que tocarlas.
    [Fact]
    public void ProcessedAtAndTheLeaseAreNullableAndAttemptsDefaultsToOne()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var inbox = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(NotificationInboxMessage))!;

        Assert.True(inbox.FindProperty(nameof(NotificationInboxMessage.ProcessedAt))!.IsNullable);
        Assert.True(inbox.FindProperty(nameof(NotificationInboxMessage.ClaimedUntil))!.IsNullable);
        var attempts = inbox.FindProperty(nameof(NotificationInboxMessage.Attempts))!;
        Assert.False(attempts.IsNullable);
        Assert.Equal(1, attempts.GetDefaultValue());
    }

    // Spec 2026-11-22 §11: the raw INSERT of the writer names these columns and indexes by hand, and the unique one is what absorbs a
    // repeated event (D-7), so a convention-made name would break it without the compiler noticing.
    [Fact]
    public void InAppNotificationsMapToTheirTableAndIndexes()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var entry = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(InAppNotification))!;

        Assert.Equal("in_app_notifications", entry.GetTableName());
        Assert.Equal("notifications", entry.GetSchema());
        Assert.Equal("jsonb", entry.FindProperty(nameof(InAppNotification.DataJson))!.GetColumnType());
        Assert.Equal("data", entry.FindProperty(nameof(InAppNotification.DataJson))!.GetColumnName());
        Assert.Equal(64, entry.FindProperty(nameof(InAppNotification.Kind))!.GetMaxLength());
        Assert.Equal(InAppNotification.DedupeKeyMaxLength, entry.FindProperty(nameof(InAppNotification.DedupeKey))!.GetMaxLength());

        var dedupe = entry.GetIndexes().Single(index => index.GetDatabaseName() == "IX_in_app_notifications_member_dedupe");
        Assert.True(dedupe.IsUnique);
        Assert.Equal(["tenant_id", "member_id", "dedupe_key"], dedupe.Properties.Select(property => property.GetColumnName()));

        var unread = entry.GetIndexes().Single(index => index.GetDatabaseName() == "IX_in_app_notifications_member_unread");
        Assert.Equal("read_at IS NULL", unread.GetFilter());
        Assert.Equal(["tenant_id", "member_id"], unread.Properties.Select(property => property.GetColumnName()));

        var list = entry.GetIndexes().Single(index => index.GetDatabaseName() == "IX_in_app_notifications_member_created");
        Assert.Equal([false, false, true, true], list.IsDescending);

        Assert.Contains(entry.GetIndexes(), index => index.GetDatabaseName() == "IX_in_app_notifications_resource");
        Assert.Contains(entry.GetIndexes(), index => index.GetDatabaseName() == "IX_in_app_notifications_created");
    }

    // The worker claims by (status, due_at) and the writer's ON CONFLICT names the unique index by column, so both are part of the contract.
    [Fact]
    public void TaskNoticeEscalationsHaveAUniqueIndexOnTheEntry()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var escalation = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TaskNoticeEscalation))!;

        Assert.Equal("task_notice_escalations", escalation.GetTableName());
        Assert.Equal("notifications", escalation.GetSchema());
        var unique = escalation.GetIndexes().Single(index => index.GetDatabaseName() == "UX_task_notice_escalations_notification");
        Assert.True(unique.IsUnique);
        Assert.Equal(["in_app_notification_id"], unique.Properties.Select(property => property.GetColumnName()));
        var due = escalation.GetIndexes().Single(index => index.GetDatabaseName() == "IX_task_notice_escalations_due");
        Assert.Equal(["status", "due_at"], due.Properties.Select(property => property.GetColumnName()));
        Assert.Equal("jsonb", escalation.FindProperty(nameof(TaskNoticeEscalation.ChannelAttemptsJson))!.GetColumnType());
    }

    // HR birthday notices: the consumer inserts these rows with raw SQL that names the columns and the unique key by hand (ON CONFLICT), and
    // the worker claims by (status, claimed_until), so a convention-made name would break both without the compiler noticing.
    [Fact]
    public void HrBirthdayDeliveriesMapToTheirTableColumnsAndKeys()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var delivery = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(HrBirthdayDelivery))!;

        Assert.Equal(("hr_birthday_deliveries", "notifications"), (delivery.GetTableName(), delivery.GetSchema()));
        Assert.Equal(
            ["attempts", "channel", "claimed_until", "completed_at", "connection_id", "created_at", "expires_at", "id", "member_id", "payload",
                "result_code", "run_date", "sent_by_member_id", "status", "tenant_id"],
            delivery.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.Equal("jsonb", delivery.FindProperty(nameof(HrBirthdayDelivery.PayloadJson))!.GetColumnType());
        Assert.Equal("date", delivery.FindProperty(nameof(HrBirthdayDelivery.RunDate))!.GetColumnType());
        var unique = delivery.GetIndexes().Single(index => index.IsUnique);
        Assert.Equal(["tenant_id", "member_id", "run_date", "channel"], unique.Properties.Select(property => property.GetColumnName()));
        Assert.Equal("UX_hr_birthday_deliveries_key", unique.GetDatabaseName());
        var pending = delivery.GetIndexes().Single(index => index.GetDatabaseName() == "IX_hr_birthday_deliveries_status");
        Assert.Equal(["status", "created_at"], pending.Properties.Select(property => property.GetColumnName()));
        Assert.True(delivery.FindProperty(nameof(HrBirthdayDelivery.ConnectionId))!.IsNullable);
    }

    [Fact]
    public void TheHrBirthdayDeliveryStatusAndChannelFitTheirColumns()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var delivery = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(HrBirthdayDelivery))!;

        var status = delivery.FindProperty(nameof(HrBirthdayDelivery.Status))!.GetMaxLength()!.Value;
        var channel = delivery.FindProperty(nameof(HrBirthdayDelivery.Channel))!.GetMaxLength()!.Value;
        Assert.All(Enum.GetNames<HrBirthdayDeliveryStatus>(), name => Assert.True(name.Length <= status));
        Assert.All([HrBirthdayDeliveryChannels.WhatsApp, HrBirthdayDeliveryChannels.Email], name => Assert.True(name.Length <= channel));
        Assert.Equal(("whatsapp", "email"), (HrBirthdayDeliveryChannels.WhatsApp, HrBirthdayDeliveryChannels.Email));
        Assert.Equal(
            ["stale", "no_phone", "no_email", "member_inactive", "attempts_exhausted"],
            [HrBirthdayDeliverySkipReasons.Stale, HrBirthdayDeliverySkipReasons.NoPhone, HrBirthdayDeliverySkipReasons.NoEmail,
                HrBirthdayDeliverySkipReasons.MemberInactive, HrBirthdayDeliverySkipReasons.AttemptsExhausted]);
    }

    [Fact]
    public void TheEscalationStatusFitsItsColumn()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var status = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TaskNoticeEscalation))!
            .FindProperty(nameof(TaskNoticeEscalation.Status))!;

        var max = status.GetMaxLength()!.Value;
        Assert.Equal(16, max);
        Assert.All(Enum.GetNames<TaskNoticeEscalationStatus>(), name => Assert.True(name.Length <= max));
    }

    [Fact]
    public void TheNewBellKindsFitTheirColumn()
    {
        Assert.All(
            [nameof(InAppNotificationKind.SupportTaskAssigned), nameof(InAppNotificationKind.SupportTicketAssigned)],
            name => Assert.True(name.Length <= 64));
    }

    [Fact]
    public void TheNotificationTenantIsOptional()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        Assert.True(model.FindEntityType(typeof(Notification))!
            .FindProperty(nameof(Notification.TenantId))!.IsNullable);
    }
}
