using Microsoft.EntityFrameworkCore;
using Modules.Notifications.Domain;

namespace Modules.Notifications.Infrastructure.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<InAppNotification> InAppNotifications => Set<InAppNotification>();

    public DbSet<TaskNoticeEscalation> TaskNoticeEscalations => Set<TaskNoticeEscalation>();

    internal DbSet<NotificationInboxMessage> Inbox => Set<NotificationInboxMessage>();

    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureNotification(modelBuilder);
        ConfigureInApp(modelBuilder);
        ConfigureTaskNoticeEscalations(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureNotification(ModelBuilder modelBuilder)
    {
        var notification = modelBuilder.Entity<Notification>();
        notification.ToTable("notifications", "notifications");
        notification.HasKey(value => value.Id);
        notification.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new NotificationId(value))
            .ValueGeneratedNever();
        notification.Property(value => value.TenantId).HasColumnName("tenant_id");
        notification.Property(value => value.RecipientId).HasColumnName("recipient_id");
        notification.Property(value => value.RecipientAddress)
            .HasColumnName("recipient_address")
            .HasMaxLength(254);
        notification.Property(value => value.Channel)
            .HasColumnName("channel")
            .HasConversion<string>()
            .HasMaxLength(20);
        notification.Property(value => value.TemplateRef)
            .HasColumnName("template_ref")
            .HasMaxLength(100);
        notification.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        notification.Property(value => value.CreatedAt).HasColumnName("created_at");
        notification.Property(value => value.SentAt).HasColumnName("sent_at");
        notification.Property(value => value.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(500);
        notification.HasIndex(value => new { value.TenantId, value.RecipientId });
    }

    // Column and index names are written by hand: the writer inserts with raw SQL (ON CONFLICT on the dedupe index, spec D-7).
    private static void ConfigureInApp(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<InAppNotification>();
        entry.ToTable("in_app_notifications", "notifications");
        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        entry.Property(value => value.TenantId).HasColumnName("tenant_id");
        entry.Property(value => value.MemberId).HasColumnName("member_id");
        entry.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(64);
        entry.Property(value => value.ResourceId).HasColumnName("resource_id");
        entry.Property(value => value.DataJson).HasColumnName("data").HasColumnType("jsonb");
        entry.Property(value => value.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(InAppNotification.DedupeKeyMaxLength);
        entry.Property(value => value.SourceMessageId).HasColumnName("source_message_id");
        entry.Property(value => value.CreatedAt).HasColumnName("created_at");
        entry.Property(value => value.ReadAt).HasColumnName("read_at");
        entry.Property(value => value.ResolvedAt).HasColumnName("resolved_at");
        entry.HasIndex(value => new { value.TenantId, value.MemberId, value.DedupeKey })
            .IsUnique()
            .HasDatabaseName("IX_in_app_notifications_member_dedupe");
        entry.HasIndex(value => new { value.TenantId, value.MemberId, value.CreatedAt, value.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_in_app_notifications_member_created");
        entry.HasIndex(value => new { value.TenantId, value.MemberId })
            .HasFilter("read_at IS NULL")
            .HasDatabaseName("IX_in_app_notifications_member_unread");
        entry.HasIndex(value => new { value.TenantId, value.Kind, value.ResourceId })
            .HasDatabaseName("IX_in_app_notifications_resource");
        entry.HasIndex(value => value.CreatedAt).HasDatabaseName("IX_in_app_notifications_created");
    }

    // Task-notices spec 2026-11-22 §5.4. Names by hand: the in-app writer inserts these rows with raw SQL and the worker claims them
    // the same way.
    private static void ConfigureTaskNoticeEscalations(ModelBuilder modelBuilder)
    {
        var escalation = modelBuilder.Entity<TaskNoticeEscalation>();
        escalation.ToTable("task_notice_escalations", "notifications");
        escalation.HasKey(value => value.Id);
        escalation.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        escalation.Property(value => value.TenantId).HasColumnName("tenant_id");
        escalation.Property(value => value.InAppNotificationId).HasColumnName("in_app_notification_id");
        escalation.Property(value => value.TicketId).HasColumnName("ticket_id");
        escalation.Property(value => value.MemberId).HasColumnName("member_id");
        escalation.Property(value => value.AssignedByMemberId).HasColumnName("assigned_by_member_id");
        escalation.Property(value => value.DueAt).HasColumnName("due_at");
        escalation.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        escalation.Property(value => value.SkipReason).HasColumnName("skip_reason").HasMaxLength(32);
        escalation.Property(value => value.ChannelAttemptsJson).HasColumnName("channel_attempts").HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");
        escalation.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        escalation.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        escalation.Property(value => value.CreatedAt).HasColumnName("created_at");
        escalation.Property(value => value.CompletedAt).HasColumnName("completed_at");
        escalation.HasIndex(value => value.InAppNotificationId).IsUnique().HasDatabaseName("UX_task_notice_escalations_notification");
        escalation.HasIndex(value => new { value.Status, value.DueAt }).HasDatabaseName("IX_task_notice_escalations_due");
        // The 90-day retention deletes the entry; the escalation finished long before.
        escalation.HasOne<InAppNotification>().WithMany().HasForeignKey(value => value.InAppNotificationId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<NotificationInboxMessage>();
        inbox.ToTable("inbox_messages", "notifications");
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Los nombres van a mano porque InboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        // Default 1 en la base: las filas que ya existen quedan con un intento, sin tocarlas.
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        // Vista de sólo lectura sobre el Outbox de plataforma, propiedad del módulo productor.
        var outbox = modelBuilder.Entity<OutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }
}
