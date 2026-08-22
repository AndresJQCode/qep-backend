using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options)
    : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    internal DbSet<TenantSettingsChangeLogEntry> TenantSettingsChangeLog =>
        Set<TenantSettingsChangeLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTenant(modelBuilder);
        ConfigureMembership(modelBuilder);
        // audit.entries es propiedad del módulo Audit; acá se mapea como proyección de
        // escritura ExcludeFromMigrations para que las auditorías críticas commiteen atómicas en
        // la transacción de este contexto (ADR 0019).
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureOutbox(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureTenantSettingsChangeLog(modelBuilder);
    }

    private static void ConfigureTenant(ModelBuilder modelBuilder)
    {
        var tenant = modelBuilder.Entity<Tenant>();
        tenant.ToTable("tenants", "tenancy");
        tenant.HasKey(value => value.Id);
        tenant.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new TenantId(value))
            .ValueGeneratedNever();
        tenant.Property(value => value.Slug)
            .HasColumnName("slug")
            .HasMaxLength(63);
        tenant.HasIndex(value => value.Slug).IsUnique();
        tenant.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        tenant.Property(value => value.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(120);
        tenant.Property(value => value.DefaultCulture)
            .HasColumnName("default_culture")
            .HasMaxLength(20);
        tenant.Property(value => value.TimeZone)
            .HasColumnName("time_zone")
            .HasMaxLength(100);
        tenant.Property(value => value.DateFormat)
            .HasColumnName("date_format")
            .HasMaxLength(30);
        tenant.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        tenant.Property(value => value.CreatedAt).HasColumnName("created_at");
        tenant.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        tenant.Ignore(value => value.DomainEvents);
    }

    private static void ConfigureMembership(ModelBuilder modelBuilder)
    {
        var membership = modelBuilder.Entity<Membership>();
        membership.ToTable("memberships", "tenancy");
        membership.HasKey(value => value.Id);
        membership.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new MembershipId(value))
            .ValueGeneratedNever();
        membership.Property(value => value.UserId).HasColumnName("user_id");
        membership.Property(value => value.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, value => new TenantId(value));
        membership.Property(value => value.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasMaxLength(20);
        membership.PrimitiveCollection<List<string>>("_roles")
            .HasColumnName("roles");
        membership.Ignore(value => value.Roles);
        membership.Property(value => value.Origin)
            .HasColumnName("origin")
            .HasMaxLength(50);
        membership.Property(value => value.InvitedAt).HasColumnName("invited_at");
        membership.Property(value => value.AcceptedAt).HasColumnName("accepted_at");
        membership.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        membership.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        membership.Property(value => value.CreatedAt).HasColumnName("created_at");
        membership.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        membership.HasIndex(value => new { value.UserId, value.TenantId }).IsUnique();
        membership.Ignore(value => value.DomainEvents);
    }

    private static void ConfigureOutbox(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<OutboxMessage>();
        message.ToTable("outbox_messages", "platform");
        message.HasKey(value => value.Id);
        message.Property(value => value.Id).HasColumnName("id");
        message.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        message.Property(value => value.PayloadJson)
            .HasColumnName("payload")
            .HasColumnType("jsonb");
        message.Property(value => value.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(100);
        message.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        message.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        message.Property(value => value.Attempts).HasColumnName("attempts");
        message.Property(value => value.LastError).HasColumnName("last_error");
        message.HasIndex(value => new { value.ProcessedAt, value.OccurredAt });
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<InboxMessage>();
        inbox.ToTable("inbox_messages", "platform");
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer)
            .HasColumnName("consumer")
            .HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
    }

    private static void ConfigureTenantSettingsChangeLog(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<TenantSettingsChangeLogEntry>();
        entry.ToTable("tenant_settings_change_log", "tenancy");
        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id).HasColumnName("id");
        entry.Property(value => value.TenantId).HasColumnName("tenant_id");
        entry.Property(value => value.EventId).HasColumnName("event_id");
        entry.Property(value => value.Version).HasColumnName("version");
        entry.Property(value => value.AppliedAt).HasColumnName("applied_at");
        entry.HasIndex(value => value.TenantId);
    }
}
