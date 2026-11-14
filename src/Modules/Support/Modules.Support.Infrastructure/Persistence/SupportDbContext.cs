using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// El esquema <c>support</c> (spec 2026-11-14). Sin FKs a otros esquemas: clientes, miembros y tenant son referencias blandas. El
/// índice único de nombre de tipo es por <c>(tenant_id, lower(name))</c> y va en la migración con SQL: EF no modela índices por
/// expresión (mismo caso que <c>AutomationsDbContext</c>).
/// </summary>
public sealed class SupportDbContext(DbContextOptions<SupportDbContext> options)
    : DbContext(options)
{
    public const string Schema = "support";

    /// <summary>Único de nombre de tipo, por <c>(tenant_id, lower(name))</c>, creado por SQL en <c>InitialSupport</c>.</summary>
    public const string TicketTypeNameIndex = "IX_ticket_types_tenant_name";

    /// <summary>Único del ordinal de la traza: dos guardados que agregan el mismo ordinal chocan acá.</summary>
    public const string TicketEventOrdinalIndex = "IX_ticket_events_ticket_ordinal";

    public DbSet<SupportTicket> Tickets => Set<SupportTicket>();

    public DbSet<SupportTicketEvent> TicketEvents => Set<SupportTicketEvent>();

    public DbSet<SupportTicketType> TicketTypes => Set<SupportTicketType>();

    public DbSet<SupportTenantSettings> TenantSettings => Set<SupportTenantSettings>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureSettings(modelBuilder);
        ConfigureTicketType(modelBuilder);
        ConfigureTicket(modelBuilder);
        ConfigureTicketEvent(modelBuilder);
    }

    // La fila la inserta SupportProvisioner con SQL crudo; EF sólo la lee y actualiza el umbral. El contador y created_at son
    // propiedades sombra: existen en el modelo para que la migración las cree, pero el dominio no las ve y EF nunca las escribe.
    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<SupportTenantSettings>();
        settings.ToTable("tenant_settings", Schema, table =>
            table.HasCheckConstraint(
                "CK_tenant_settings_due_soon",
                $"due_soon_business_days BETWEEN {SupportTenantSettings.MinDueSoonBusinessDays} AND {SupportTenantSettings.MaxDueSoonBusinessDays}"));
        settings.HasKey(value => value.TenantId);
        settings.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        settings.Property<long>("NextTicketNumber").HasColumnName("next_ticket_number").HasDefaultValue(1L);
        settings.Property(value => value.DueSoonBusinessDays)
            .HasColumnName("due_soon_business_days")
            .HasDefaultValue(SupportTenantSettings.DefaultDueSoonBusinessDays);
        settings.Property<DateTimeOffset>("CreatedAt").HasColumnName("created_at");
        settings.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        settings.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .HasDefaultValue(1L)
            .IsConcurrencyToken();
    }

    private static void ConfigureTicketType(ModelBuilder modelBuilder)
    {
        var type = modelBuilder.Entity<SupportTicketType>();
        type.ToTable("ticket_types", Schema, table =>
        {
            table.HasCheckConstraint(
                "CK_ticket_types_term",
                $"term_business_days IS NULL OR term_business_days BETWEEN 1 AND {SupportTicketType.MaxTermBusinessDays}");
            table.HasCheckConstraint("CK_ticket_types_legal_term", "NOT term_is_legal OR term_business_days IS NOT NULL");
        });
        type.HasKey(value => value.Id);
        type.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new SupportTicketTypeId(value))
            .ValueGeneratedNever();
        type.Property(value => value.TenantId).HasColumnName("tenant_id");
        type.Property(value => value.Name).HasColumnName("name").HasMaxLength(SupportTicketType.NameMaxLength);
        type.Property(value => value.TermBusinessDays).HasColumnName("term_business_days");
        type.Property(value => value.TermIsLegal).HasColumnName("term_is_legal");
        type.Property(value => value.RequiresFormalResponse).HasColumnName("requires_formal_response");
        type.Property(value => value.IsPublished).HasColumnName("is_published");
        type.Property(value => value.IsActive).HasColumnName("is_active");
        type.Property(value => value.Position).HasColumnName("position");
        type.Property(value => value.CreatedAt).HasColumnName("created_at");
        type.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        type.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
    }

    // Los enums van como texto de 24 y `version` como bigint fijado a mano. Los CHECK repiten los valores del dominio para que
    // la base no dependa de quien escribe; los de entrada y canal listan desde ya los de las fases 2 y 3 (plan D8).
    private static void ConfigureTicket(ModelBuilder modelBuilder)
    {
        var ticket = modelBuilder.Entity<SupportTicket>();
        ticket.ToTable("tickets", Schema, table =>
        {
            table.HasCheckConstraint("CK_tickets_status", InList("status", Enum.GetNames<SupportTicketStatus>()));
            table.HasCheckConstraint("CK_tickets_intake", InList("intake", Enum.GetNames<SupportTicketIntake>()));
            table.HasCheckConstraint("CK_tickets_reply_channel", InList("reply_channel", Enum.GetNames<SupportReplyChannel>()));
        });
        ticket.HasKey(value => value.Id);
        ticket.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value))
            .ValueGeneratedNever();
        ticket.Property(value => value.TenantId).HasColumnName("tenant_id");
        ticket.Property(value => value.Number).HasColumnName("number");
        // El tipo vive en el mismo esquema, pero el ticket guarda su foto: no hace falta la FK y un tipo nunca se borra.
        ticket.Property(value => value.TypeId)
            .HasColumnName("ticket_type_id")
            .HasConversion(id => id.Value, value => new SupportTicketTypeId(value));
        ticket.Property(value => value.TypeName).HasColumnName("type_name").HasMaxLength(SupportTicketType.NameMaxLength);
        ticket.Property(value => value.TermBusinessDays).HasColumnName("term_business_days");
        ticket.Property(value => value.TermIsLegal).HasColumnName("term_is_legal");
        ticket.Property(value => value.RequiresFormalResponse).HasColumnName("requires_formal_response");
        ticket.Property(value => value.Subject).HasColumnName("subject").HasMaxLength(SupportTicket.SubjectMaxLength);
        ticket.Property(value => value.Description).HasColumnName("description").HasMaxLength(SupportTicket.DescriptionMaxLength);
        ticket.Property(value => value.Intake).HasColumnName("intake").HasConversion<string>().HasMaxLength(24);
        ticket.Property(value => value.ManualMedium).HasColumnName("manual_medium").HasConversion<string>().HasMaxLength(24);
        ticket.Property(value => value.ReplyChannel).HasColumnName("reply_channel").HasConversion<string>().HasMaxLength(24);
        ticket.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24);
        // Responsable y cliente son referencias blandas: viven en otros esquemas.
        ticket.Property(value => value.AssigneeMemberId).HasColumnName("assignee_member_id");
        ticket.Property(value => value.CustomerId).HasColumnName("customer_id");
        ticket.OwnsOne(value => value.Requester, requester =>
        {
            requester.Property(value => value.FirstNames).HasColumnName("requester_first_names").HasMaxLength(TicketRequester.NameMaxLength);
            requester.Property(value => value.LastNames).HasColumnName("requester_last_names").HasMaxLength(TicketRequester.NameMaxLength);
            requester.Property(value => value.BusinessName)
                .HasColumnName("requester_business_name")
                .HasMaxLength(TicketRequester.NameMaxLength);
            requester.Property(value => value.DocumentType)
                .HasColumnName("requester_document_type")
                .HasMaxLength(TicketRequester.DocumentTypeMaxLength);
            requester.Property(value => value.DocumentNumber)
                .HasColumnName("requester_document_number")
                .HasMaxLength(TicketRequester.DocumentNumberMaxLength);
            requester.Property(value => value.Email).HasColumnName("requester_email").HasMaxLength(TicketRequester.EmailMaxLength);
            requester.Property(value => value.MobilePhone)
                .HasColumnName("requester_mobile_phone")
                .HasMaxLength(TicketRequester.MobilePhoneMaxLength);
            requester.Property(value => value.LandlinePhone)
                .HasColumnName("requester_landline_phone")
                .HasMaxLength(TicketRequester.LandlinePhoneMaxLength);
            // Nullable en la base aunque el record lo exija: sin solicitante (sólo cliente) todas las columnas van en NULL.
            requester.Property(value => value.CountryCode)
                .HasColumnName("requester_country_code")
                .HasMaxLength(2)
                .IsRequired(false);
            requester.Property(value => value.Address).HasColumnName("requester_address").HasMaxLength(TicketRequester.AddressMaxLength);
        });
        ticket.Property(value => value.ReceivedAt).HasColumnName("received_at");
        ticket.Property(value => value.ReceivedOn).HasColumnName("received_on");
        ticket.Property(value => value.DueOn).HasColumnName("due_on");
        ticket.Property(value => value.PausedSince).HasColumnName("paused_since");
        ticket.Property(value => value.PausedBusinessDays).HasColumnName("paused_business_days").HasDefaultValue(0);
        ticket.Property(value => value.ResolutionCount).HasColumnName("resolution_count").HasDefaultValue(0);
        ticket.Property(value => value.ResolvedAt).HasColumnName("resolved_at");
        ticket.Property(value => value.ClosedAt).HasColumnName("closed_at");
        ticket.Property(value => value.LastEventOrdinal).HasColumnName("last_event_ordinal");
        ticket.Property(value => value.CreatedAt).HasColumnName("created_at");
        ticket.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        ticket.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        ticket.Ignore(value => value.DisplayNumber);
        ticket.HasMany(value => value.Events)
            .WithOne()
            .HasForeignKey(value => value.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        ticket.Navigation(value => value.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        ticket.HasIndex(value => new { value.TenantId, value.Number }).IsUnique().HasDatabaseName("IX_tickets_tenant_number");
        ticket.HasIndex(value => new { value.TenantId, value.Status, value.ReceivedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_tickets_tenant_status_received");
        ticket.HasIndex(value => new { value.TenantId, value.DueOn }).HasDatabaseName("IX_tickets_tenant_due");
        ticket.HasIndex(value => new { value.TenantId, value.CustomerId }).HasDatabaseName("IX_tickets_tenant_customer");
    }

    private static void ConfigureTicketEvent(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<SupportTicketEvent>();
        item.ToTable("ticket_events", Schema);
        item.HasKey(value => value.Id);
        item.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        item.Property(value => value.TenantId).HasColumnName("tenant_id");
        item.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        item.Property(value => value.Ordinal).HasColumnName("ordinal");
        item.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(24);
        item.Property(value => value.ActorMemberId).HasColumnName("actor_member_id");
        item.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        item.Property(value => value.Details).HasColumnName("details").HasColumnType("jsonb");
        item.HasIndex(value => new { value.TicketId, value.Ordinal }).IsUnique().HasDatabaseName(TicketEventOrdinalIndex);
    }

    private static string InList(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(',', values.Select(value => $"'{value}'"))})";
}
