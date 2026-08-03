using Microsoft.EntityFrameworkCore;
using Modules.Notifications.Domain;

namespace Modules.Notifications.Infrastructure.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    internal DbSet<NotificationInboxMessage> Inbox => Set<NotificationInboxMessage>();

    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureNotification(modelBuilder);
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

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<NotificationInboxMessage>();
        inbox.ToTable("inbox_messages", "notifications");
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        // Read-only view over the platform Outbox owned by the producing module.
        var outbox = modelBuilder.Entity<OutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }
}
