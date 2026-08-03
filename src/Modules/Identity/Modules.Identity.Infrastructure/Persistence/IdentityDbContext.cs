using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Session> Sessions => Set<Session>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<IdentityInboxMessage> Inbox => Set<IdentityInboxMessage>();

    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUser(modelBuilder);
        ConfigureProviderLink(modelBuilder);
        ConfigureSession(modelBuilder);
        // audit.entries is owned by the Audit module; map it here as an
        // ExcludeFromMigrations write projection so session issue/revoke audits commit
        // atomically in this context's transaction (ADR 0019), same as Tenancy does.
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureUser(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<User>();
        user.ToTable("users", "identity");
        user.HasKey(value => value.Id);
        user.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new UserId(value))
            .ValueGeneratedNever();
        user.Property(value => value.Email)
            .HasColumnName("email")
            .HasMaxLength(254);
        user.HasIndex(value => value.Email).IsUnique();
        user.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        user.Property(value => value.CreatedAt).HasColumnName("created_at");
        user.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        user.HasMany(value => value.ProviderLinks)
            .WithOne()
            .HasForeignKey(link => link.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        user.Navigation(value => value.ProviderLinks)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureProviderLink(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<ProviderLink>();
        link.ToTable("provider_links", "identity");
        link.HasKey(value => value.Id);
        link.Property(value => value.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        link.Property(value => value.UserId)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value));
        link.Property(value => value.Provider)
            .HasColumnName("provider")
            .HasMaxLength(50);
        link.Property(value => value.Subject)
            .HasColumnName("subject")
            .HasMaxLength(255);
        link.Property(value => value.LinkedAt).HasColumnName("linked_at");
        link.HasIndex(value => new { value.Provider, value.Subject }).IsUnique();
    }

    private static void ConfigureSession(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<Session>();
        session.ToTable("sessions", "identity");
        session.HasKey(value => value.Id);
        session.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new SessionId(value))
            .ValueGeneratedNever();
        session.Property(value => value.UserId)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value));
        session.Property(value => value.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(64);
        session.HasIndex(value => value.TokenHash).IsUnique();
        session.Property(value => value.CreatedAt).HasColumnName("created_at");
        session.Property(value => value.LastSeenAt).HasColumnName("last_seen_at");
        session.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        session.Property(value => value.RevokedAt).HasColumnName("revoked_at");
        session.Property(value => value.RevokedReason)
            .HasColumnName("revoked_reason")
            .HasMaxLength(100);
        session.Property(value => value.UserAgent)
            .HasColumnName("user_agent")
            .HasMaxLength(300);
        session.Property(value => value.IpAddress)
            .HasColumnName("ip_address")
            .HasMaxLength(45);
        session.HasIndex(value => new { value.UserId, value.RevokedAt });
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<IdentityInboxMessage>();
        inbox.ToTable("inbox_messages", "identity");
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
