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

    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

    public DbSet<PasswordCredential> PasswordCredentials => Set<PasswordCredential>();

    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();

    public DbSet<EmailChallenge> EmailChallenges => Set<EmailChallenge>();

    public DbSet<TermsAcceptance> TermsAcceptances => Set<TermsAcceptance>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<IdentityInboxMessage> Inbox => Set<IdentityInboxMessage>();

    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUser(modelBuilder);
        ConfigureProviderLink(modelBuilder);
        ConfigureSession(modelBuilder);
        ConfigurePasswordCredential(modelBuilder);
        ConfigureEmailToken(modelBuilder);
        ConfigureEmailChallenge(modelBuilder);
        ConfigureTermsAcceptance(modelBuilder);
        ConfigureUserPreference(modelBuilder);
        // audit.entries es propiedad del módulo Audit; acá se mapea como proyección de
        // escritura ExcludeFromMigrations para que las auditorías de emisión/revocación de sesión
        // commiteen atómicas en la transacción de este contexto (ADR 0019), igual que Tenancy.
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
        user.Property(value => value.EmailVerifiedAt).HasColumnName("email_verified_at");

        user.HasMany(value => value.ProviderLinks)
            .WithOne()
            .HasForeignKey(link => link.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        user.Navigation(value => value.ProviderLinks)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    /// <summary>
    /// ACC-03. La clave es el par <c>(user_id, tenant_id)</c>: la preferencia es del usuario
    /// en cada tenant (SDD-OD-17).
    /// </summary>
    private static void ConfigureUserPreference(ModelBuilder modelBuilder)
    {
        var preference = modelBuilder.Entity<UserPreference>();
        preference.ToTable("user_preferences", "identity");
        preference.HasKey(value => new { value.UserId, value.TenantId });
        preference.Property(value => value.UserId)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value));
        // Sin clave foránea, y a propósito: `tenants` vive en el esquema de Tenancy y ningún
        // módulo referencia tablas de otro — ArchitectureTests lo verifica. La integridad la
        // da que este tenant_id siempre pasó por la verificación de membresía de
        // ExternalClaimsTransformation antes de llegar acá.
        preference.Property(value => value.TenantId).HasColumnName("tenant_id");
        preference.Property(value => value.ColorScheme)
            .HasColumnName("color_scheme")
            .HasMaxLength(32);
        preference.Property(value => value.Mode)
            .HasColumnName("mode")
            .HasConversion<string>()
            .HasMaxLength(10);
        // Spec 2026-10-14, decisión 7. Default en la columna: las filas anteriores a la migración quedan
        // con el sonido encendido sin reescribirlas, que es lo mismo que ve quien nunca eligió.
        preference.Property(value => value.MessageSound)
            .HasColumnName("message_sound")
            .HasDefaultValue(true);
        preference.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // Nullable a propósito: null es «el del tenant» (spec 2026-11-22, i18n).
        preference.Property(value => value.Language).HasColumnName("language").HasMaxLength(10);

        preference.HasOne<User>()
            .WithMany()
            .HasForeignKey(value => value.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigurePasswordCredential(ModelBuilder modelBuilder)
    {
        var credential = modelBuilder.Entity<PasswordCredential>();
        credential.ToTable("password_credentials", "identity");
        credential.HasKey(value => value.UserId);
        credential.Property(value => value.UserId)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value))
            .ValueGeneratedNever();
        credential.Property(value => value.Hash).HasColumnName("hash");
        credential.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        credential.HasOne<User>()
            .WithOne()
            .HasForeignKey<PasswordCredential>(value => value.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureEmailToken(ModelBuilder modelBuilder)
    {
        var token = modelBuilder.Entity<EmailToken>();
        token.ToTable("email_tokens", "identity");
        token.HasKey(value => value.Id);
        token.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new EmailTokenId(value))
            .ValueGeneratedNever();
        token.Property(value => value.UserId)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value));
        // Minúscula explícita: TryConsumeAsync filtra por este texto en SQL crudo.
        token.Property(value => value.Purpose)
            .HasColumnName("purpose")
            .HasConversion(
                purpose => EmailTokenRepository.PurposeToColumn(purpose),
                value => EmailTokenRepository.PurposeFromColumn(value))
            .HasMaxLength(10);
        token.Property(value => value.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(64);
        token.HasIndex(value => value.TokenHash).IsUnique();
        token.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        token.Property(value => value.UsedAt).HasColumnName("used_at");
        token.Property(value => value.CreatedAt).HasColumnName("created_at");
        token.HasIndex(value => new { value.UserId, value.Purpose, value.UsedAt });
        token.HasOne<User>()
            .WithMany()
            .HasForeignKey(value => value.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// Spec 2026-11-19 (onboarding por link): sin clave foránea a <c>users</c> porque el correo todavía no es de nadie. Los
    /// nombres de columna van a mano porque <c>EmailChallengeRepository.TryConsumeAsync</c> los escribe en SQL crudo.
    /// <c>code_hash</c> no es único: dos correos pueden recibir el mismo código de seis dígitos.
    /// </summary>
    private static void ConfigureEmailChallenge(ModelBuilder modelBuilder)
    {
        var challenge = modelBuilder.Entity<EmailChallenge>();
        challenge.ToTable("email_challenges", "identity");
        challenge.HasKey(value => value.Id);
        challenge.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new EmailChallengeId(value))
            .ValueGeneratedNever();
        challenge.Property(value => value.EmailHash)
            .HasColumnName("email_hash")
            .HasMaxLength(64);
        challenge.Property(value => value.CodeHash)
            .HasColumnName("code_hash")
            .HasMaxLength(64);
        challenge.Property(value => value.Attempts).HasColumnName("attempts");
        challenge.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        challenge.Property(value => value.UsedAt).HasColumnName("used_at");
        challenge.Property(value => value.CreatedAt).HasColumnName("created_at");
        challenge.HasIndex(value => new { value.EmailHash, value.UsedAt });
    }

    /// <summary>
    /// Spec 2026-11-19 (onboarding por link): única en <c>(user_id, version)</c>; <c>TermsAcceptanceRepository</c> la usa en
    /// un <c>ON CONFLICT</c> de SQL crudo, por eso los nombres de columna van a mano. Se borra con el usuario, como sus
    /// credenciales.
    /// </summary>
    private static void ConfigureTermsAcceptance(ModelBuilder modelBuilder)
    {
        var acceptance = modelBuilder.Entity<TermsAcceptance>();
        acceptance.ToTable("terms_acceptances", "identity");
        acceptance.HasKey(value => value.Id);
        acceptance.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new TermsAcceptanceId(value))
            .ValueGeneratedNever();
        acceptance.Property(value => value.UserId)
            .HasColumnName("user_id")
            .HasConversion(id => id.Value, value => new UserId(value));
        acceptance.Property(value => value.Version)
            .HasColumnName("version")
            .HasMaxLength(TermsAcceptance.VersionMaxLength);
        acceptance.Property(value => value.AcceptedAt).HasColumnName("accepted_at");
        acceptance.HasIndex(value => new { value.UserId, value.Version }).IsUnique();
        acceptance.HasOne<User>()
            .WithMany()
            .HasForeignKey(value => value.UserId)
            .OnDelete(DeleteBehavior.Cascade);
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
        // Los nombres van a mano porque IdentityInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        // Default 1 en la base: las filas que ya existen quedan con un intento, sin tocarlas.
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        // Proyección del Outbox de plataforma (propiedad de Tenancy): se lee y se escribe, pero la
        // tabla no se migra desde acá. Las columnas NOT NULL salen de InitialPlatform.
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
