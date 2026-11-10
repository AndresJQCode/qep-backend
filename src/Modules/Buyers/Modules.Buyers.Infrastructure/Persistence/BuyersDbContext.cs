using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Infrastructure.Persistence;

public sealed class BuyersDbContext(DbContextOptions<BuyersDbContext> options) : DbContext(options)
{
    public const string Schema = "buyers";

    internal const string ProcessedSignupsKey = "PK_processed_signups";

    public DbSet<BuyerAccount> Accounts => Set<BuyerAccount>();

    public DbSet<BuyerProfile> Profiles => Set<BuyerProfile>();

    public DbSet<BuyerSession> Sessions => Set<BuyerSession>();

    public DbSet<BuyerEmailToken> EmailTokens => Set<BuyerEmailToken>();

    public DbSet<BuyerConsent> Consents => Set<BuyerConsent>();

    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    internal DbSet<BuyerInboxMessage> Inbox => Set<BuyerInboxMessage>();

    internal DbSet<BuyerProcessedSignup> ProcessedSignups => Set<BuyerProcessedSignup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAccount(modelBuilder);
        ConfigureProfile(modelBuilder);
        ConfigureSession(modelBuilder);
        ConfigureEmailToken(modelBuilder);
        ConfigureConsent(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureProcessedSignup(modelBuilder);
    }

    private static void ConfigureAccount(ModelBuilder modelBuilder)
    {
        var account = modelBuilder.Entity<BuyerAccount>();
        account.ToTable("accounts", Schema);
        account.HasKey(value => value.Id);
        account.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        account.Property(value => value.Email).HasColumnName("email").HasMaxLength(BuyerEmail.MaxLength);
        account.HasIndex(value => value.Email).IsUnique().HasDatabaseName(BuyersUnitOfWork.EmailIndex);
        account.Property(value => value.PasswordHash).HasColumnName("password_hash").HasColumnType("text");
        account.Property(value => value.EmailVerifiedAt).HasColumnName("email_verified_at");
        account.Property(value => value.CreatedAt).HasColumnName("created_at");
        account.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // M2: el xmin de PostgreSQL como token de concurrencia (propiedad sombra, sin columna nueva). Dos escrituras sobre la misma
        // cuenta leída en el mismo estado no se pisan: la segunda sale como RequestConcurrencyException (BuyersUnitOfWork).
        account.Property<uint>("Version").IsRowVersion();
        account.Ignore(value => value.IsVerified);
    }

    private static void ConfigureProfile(ModelBuilder modelBuilder)
    {
        var profile = modelBuilder.Entity<BuyerProfile>();
        profile.ToTable("profiles", Schema);
        profile.HasKey(value => value.AccountId);
        profile.Property(value => value.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        // Una foto que sólo se precarga: nunca se consulta por campo, así que va entera como jsonb.
        profile.Property(value => value.Data)
            .HasColumnName("data")
            .HasColumnType("jsonb")
            .HasConversion(
                data => JsonSerializer.Serialize(data, JsonSerializerOptions.Web),
                json => JsonSerializer.Deserialize<BuyerProfileData>(json, JsonSerializerOptions.Web)!);
        profile.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        profile.HasOne<BuyerAccount>()
            .WithOne()
            .HasForeignKey<BuyerProfile>(value => value.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureSession(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<BuyerSession>();
        session.ToTable("sessions", Schema);
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        session.Property(value => value.AccountId).HasColumnName("account_id");
        session.Property(value => value.StoreHost).HasColumnName("store_host").HasMaxLength(253);
        session.Property(value => value.TokenHash).HasColumnName("token_hash").HasColumnType("char(64)");
        session.HasIndex(value => value.TokenHash).IsUnique().HasDatabaseName("IX_sessions_token_hash");
        session.Property(value => value.CreatedAt).HasColumnName("created_at");
        session.Property(value => value.LastSeenAt).HasColumnName("last_seen_at");
        session.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        session.Property(value => value.RevokedAt).HasColumnName("revoked_at");
        session.Property(value => value.RevokedReason)
            .HasColumnName("revoked_reason")
            .HasMaxLength(BuyerSession.MaxRevokedReasonLength);
        session.Property(value => value.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        session.HasIndex(value => new { value.AccountId, value.RevokedAt });
        session.HasOne<BuyerAccount>()
            .WithMany()
            .HasForeignKey(value => value.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureEmailToken(ModelBuilder modelBuilder)
    {
        var token = modelBuilder.Entity<BuyerEmailToken>();
        token.ToTable(
            "email_tokens",
            Schema,
            table => table.HasCheckConstraint("CK_email_tokens_purpose", "purpose IN ('Verify','Reset')"));
        token.HasKey(value => value.Id);
        token.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        token.Property(value => value.AccountId).HasColumnName("account_id");
        // El nombre del enum tal cual ('Verify', 'Reset'): TryConsumeAsync filtra por este texto en SQL crudo.
        token.Property(value => value.Purpose)
            .HasColumnName("purpose")
            .HasConversion<string>()
            .HasMaxLength(16);
        token.Property(value => value.TokenHash).HasColumnName("token_hash").HasColumnType("char(64)");
        token.HasIndex(value => value.TokenHash).IsUnique().HasDatabaseName("IX_email_tokens_token_hash");
        token.Property(value => value.CreatedAt).HasColumnName("created_at");
        token.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        token.Property(value => value.UsedAt).HasColumnName("used_at");
        token.HasIndex(value => new { value.AccountId, value.Purpose, value.UsedAt });
        token.HasOne<BuyerAccount>()
            .WithMany()
            .HasForeignKey(value => value.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureConsent(ModelBuilder modelBuilder)
    {
        var consent = modelBuilder.Entity<BuyerConsent>();
        consent.ToTable("consents", Schema);
        consent.HasKey(value => value.Id);
        consent.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        // Sin clave foránea, y a propósito: el registro de la autorización sobrevive a la cuenta.
        consent.Property(value => value.AccountId).HasColumnName("account_id");
        consent.Property(value => value.Email).HasColumnName("email").HasMaxLength(BuyerEmail.MaxLength);
        consent.Property(value => value.AcceptedAt).HasColumnName("accepted_at");
        consent.Property(value => value.VisitorIp).HasColumnName("visitor_ip").HasMaxLength(45);
        consent.Property(value => value.StoreHost).HasColumnName("store_host").HasMaxLength(253);
        consent.Property(value => value.PolicyUrl).HasColumnName("policy_url").HasMaxLength(300);
        consent.Property(value => value.PolicyVersion).HasColumnName("policy_version").HasMaxLength(32);
        consent.HasIndex(value => value.AccountId).HasDatabaseName("IX_consents_account_id");
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<BuyerInboxMessage>();
        inbox.ToTable("inbox_messages", Schema);
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        // Los nombres van a mano porque BuyersInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(100);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
    }

    private static void ConfigureProcessedSignup(ModelBuilder modelBuilder)
    {
        var processed = modelBuilder.Entity<BuyerProcessedSignup>();
        processed.ToTable("processed_signups", Schema);
        processed.HasKey(value => value.OrderId).HasName(ProcessedSignupsKey);
        processed.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        processed.Property(value => value.ProcessedAt).HasColumnName("processed_at");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        // Proyección del Outbox de plataforma (propiedad de Tenancy): se escribe, pero la tabla no se migra
        // desde acá. Las columnas NOT NULL salen de InitialPlatform.
        var outbox = modelBuilder.Entity<OutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
    }
}
