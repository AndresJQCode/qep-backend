using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Infrastructure.Persistence;

/// <summary>
/// Distribuidores y sus invitaciones. Sin FKs a otros esquemas: listas de precios (en Catalog desde el spec 2026-11-22), miembros,
/// empresas, tenants y clientes son referencias blandas.
/// </summary>
public sealed class WholesaleDbContext(DbContextOptions<WholesaleDbContext> options)
    : DbContext(options)
{
    public const string Schema = "wholesale";

    /// <summary>D-13: un documento por mayorista en cualquier estado → 409 <c>identification_taken</c>.</summary>
    public const string DistributorIdentificationIndex = "IX_distributors_tenant_identification";

    /// <summary>Un tenant distribuidor tiene a lo sumo una relación con cada mayorista → 409 <c>already_linked</c>.</summary>
    public const string DistributorTenantIndex = "IX_distributors_tenant_distributor_tenant";

    /// <summary>El hash del token es la llave del link.</summary>
    public const string InvitationTokenHashIndex = "IX_distributor_invitations_token_hash";

    /// <summary>Un id reservado por invitación (D-17).</summary>
    public const string InvitationReservedTenantIndex = "IX_distributor_invitations_reserved_tenant";

    private const string DistributorIdColumn = "DistributorId";

    public DbSet<Distributor> Distributors => Set<Distributor>();

    // Proyecciones de escritura (ExcludeFromMigrations): Audit es dueño de audit.entries y Tenancy de platform.outbox_messages.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<WholesaleOutboxMessage> Outbox => Set<WholesaleOutboxMessage>();

    // Spec 2026-11-18: el inbox del consumidor del aviso por WhatsApp (InvitationNoticeProcessor), como Compras.
    internal DbSet<WholesaleInboxMessage> Inbox => Set<WholesaleInboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureDistributor(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        ConfigureInbox(modelBuilder);
    }

    private static void ConfigureDistributor(ModelBuilder modelBuilder)
    {
        var distributor = modelBuilder.Entity<Distributor>();
        distributor.ToTable("distributors", Schema);
        distributor.HasKey(value => value.Id);
        distributor.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new DistributorId(value))
            .ValueGeneratedNever();
        distributor.Property(value => value.TenantId).HasColumnName("tenant_id");
        distributor.Property(value => value.BusinessName)
            .HasColumnName("business_name")
            .HasMaxLength(DistributorTextLimits.BusinessName);
        // Spec 2026-11-18: el país decide qué tipos de documento admite; el tipo entra en la unicidad.
        distributor.Property(value => value.Country).HasColumnName("country").HasMaxLength(2);
        distributor.Property(value => value.IdentificationType).HasColumnName("identification_type").HasMaxLength(16);
        distributor.Property(value => value.IdentificationNumber)
            .HasColumnName("identification_number")
            .HasMaxLength(DistributorTextLimits.IdentificationNumber);
        distributor.Property(value => value.ContactName)
            .HasColumnName("contact_name")
            .HasMaxLength(DistributorTextLimits.ContactName);
        distributor.Property(value => value.Email).HasColumnName("email").HasMaxLength(DistributorTextLimits.Email);
        distributor.Property(value => value.PriceListId)
            .HasColumnName("price_list_id")
            .HasConversion(id => id.Value, value => new PriceListId(value));
        distributor.Property(value => value.AdvisorMemberId).HasColumnName("advisor_member_id");
        distributor.Property(value => value.BillingCompanyId).HasColumnName("billing_company_id");
        distributor.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        distributor.Property(value => value.DistributorTenantId).HasColumnName("distributor_tenant_id");
        distributor.Property(value => value.CustomerId).HasColumnName("customer_id");
        distributor.Property(value => value.InvitedByUserId).HasColumnName("invited_by_user_id");
        distributor.Property(value => value.CreatedAt).HasColumnName("created_at");
        distributor.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        distributor.Property(value => value.ActivatedAt).HasColumnName("activated_at");
        // La marca de «empezó a aceptar»: se confirma antes de escribir en otro módulo y bloquea el cambio de documento.
        distributor.Property(value => value.AcceptanceStartedAt).HasColumnName("acceptance_started_at");
        distributor.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        // Spec 2026-11-18: el destino del aviso por WhatsApp; la conexión es referencia blanda a Integrations.
        distributor.Property(value => value.Phone).HasColumnName("phone").HasMaxLength(Distributor.PhoneMaxLength);
        distributor.Property(value => value.WhatsAppConnectionId).HasColumnName("whatsapp_connection_id");
        // Ciudad y dirección con las que nace la empresa del distribuidor al aceptar; la ciudad es referencia blanda a Geography.
        distributor.Property(value => value.CityId).HasColumnName("city_id");
        distributor.Property(value => value.CityName).HasColumnName("city_name").HasMaxLength(Distributor.CityNameMaxLength);
        distributor.Property(value => value.Address).HasColumnName("address").HasMaxLength(Distributor.AddressMaxLength);

        distributor.HasIndex(value => new { value.TenantId, value.IdentificationType, value.IdentificationNumber })
            .IsUnique()
            .HasDatabaseName(DistributorIdentificationIndex);
        distributor.HasIndex(value => new { value.TenantId, value.DistributorTenantId })
            .IsUnique()
            .HasFilter("distributor_tenant_id IS NOT NULL")
            .HasDatabaseName(DistributorTenantIndex);
        // El orden de la lista: created_at DESC, id DESC.
        distributor.HasIndex(value => new { value.TenantId, value.CreatedAt, value.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_distributors_tenant_created");
        distributor.HasIndex(value => new { value.TenantId, value.Status }).HasDatabaseName("IX_distributors_tenant_status");
        distributor.HasIndex(value => new { value.TenantId, value.PriceListId }).HasDatabaseName("IX_distributors_tenant_price_list");

        // D-1: la invitación es hija 1:1 con la llave del distribuidor; viaja siempre con él y se borra con él (cascade).
        // Sólo el hash: el token plano no tiene columna.
        distributor.OwnsOne(value => value.Invitation, invitation =>
        {
            invitation.ToTable("distributor_invitations", Schema);
            invitation.WithOwner().HasForeignKey(DistributorIdColumn);
            invitation.Property<DistributorId>(DistributorIdColumn)
                .HasColumnName("distributor_id")
                .HasConversion(id => id.Value, value => new DistributorId(value));
            invitation.HasKey(DistributorIdColumn);
            invitation.Property(value => value.TokenHash).HasColumnName("token_hash").HasColumnType("char(64)");
            // Spec 2026-11-24: como el token, sólo el hash; la prueba plana no tiene columna. Sin índice: se compara sobre la
            // invitación que ya se encontró por token.
            invitation.Property(value => value.EmailProofHash).HasColumnName("email_proof_hash").HasColumnType("char(64)");
            invitation.Property(value => value.SentAt).HasColumnName("sent_at");
            invitation.Property(value => value.ExpiresAt).HasColumnName("expires_at");
            invitation.Property(value => value.ReservedTenantId).HasColumnName("reserved_tenant_id");
            invitation.Property(value => value.AcceptedAt).HasColumnName("accepted_at");
            invitation.Property(value => value.AcceptedByUserId).HasColumnName("accepted_by_user_id");
            // Las invitaciones de antes del aviso quedan en la generación 0 y sin aviso.
            // Token de concurrencia (D-4): el resultado de un aviso leído antes de un PUT o un «Reenviar» no se escribe sobre la
            // generación nueva; el UPDATE no encuentra la fila y el mensaje vuelve, ya viejo.
            invitation.Property(value => value.Generation)
                .HasColumnName("generation")
                .HasDefaultValue(0)
                .IsConcurrencyToken();
            invitation.Property(value => value.NoticeStatus)
                .HasColumnName("notice_status")
                .HasConversion<string>()
                .HasMaxLength(16);
            invitation.Property(value => value.NoticeFailureCode)
                .HasColumnName("notice_failure_code")
                .HasMaxLength(DistributorInvitation.NoticeFailureCodeMaxLength);
            invitation.HasIndex(value => value.TokenHash).IsUnique().HasDatabaseName(InvitationTokenHashIndex);
            invitation.HasIndex(value => value.ReservedTenantId).IsUnique().HasDatabaseName(InvitationReservedTenantIndex);
        });
        distributor.Navigation(value => value.Invitation).IsRequired();
    }

    // Copia de PurchasesDbContext.ConfigureInbox.
    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<WholesaleInboxMessage>();
        inbox.ToTable("inbox_messages", Schema);
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Los nombres van a mano porque WholesaleInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    // Forma de SuppliersDbContext.ConfigureOutboxProjection.
    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<WholesaleOutboxMessage>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
        outbox.Property(value => value.LastError).HasColumnName("last_error");
    }
}

/// <summary>
/// Proyección de escritura del outbox de plataforma, propiedad de Tenancy. Mayorista inserta acá la invitación en la misma
/// transacción que la relación; la consume el worker de correo de Notifications.
/// </summary>
internal sealed class WholesaleOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
