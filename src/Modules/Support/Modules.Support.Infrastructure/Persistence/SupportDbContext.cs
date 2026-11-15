using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

    /// <summary>Único del mensaje por ticket (plan E3): dos réplicas del seguimiento sobre el mismo entrante chocan acá.</summary>
    public const string TicketMessageIndex = "IX_ticket_messages_ticket_message";

    /// <summary>La PK de los entrantes sin clasificar: un mensaje se registra una sola vez (plan E3).</summary>
    public const string UnclassifiedMessageKey = "PK_unclassified_messages";

    /// <summary>Único del <c>clientId</c> por ticket (plan E16): un reintento de la misma respuesta choca acá.</summary>
    public const string TicketReplyClientIndex = "IX_ticket_replies_ticket_client";

    public DbSet<SupportTicketType> TicketTypes => Set<SupportTicketType>();

    public DbSet<SupportTenantSettings> TenantSettings => Set<SupportTenantSettings>();

    public DbSet<SupportTicketMessage> TicketMessages => Set<SupportTicketMessage>();

    public DbSet<SupportUnclassifiedMessage> UnclassifiedMessages => Set<SupportUnclassifiedMessage>();

    public DbSet<SupportTicketReply> TicketReplies => Set<SupportTicketReply>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureSettings(modelBuilder);
        ConfigureTicketType(modelBuilder);
        ConfigureTicket(modelBuilder);
        ConfigureTicketEvent(modelBuilder);
        ConfigureTicketMessage(modelBuilder);
        ConfigureUnclassifiedMessage(modelBuilder);
        ConfigureTicketReply(modelBuilder);
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
        // Sin HasDefaultValue en el modelo: con él EF tomaría el 0 de C# como «sin valor» y escribiría el default, y 0 es un umbral
        // válido (apaga «por vencer»). El DEFAULT 3 de la columna sí existe (migración InitialSupport): lo usa el INSERT del
        // aprovisionador.
        settings.Property(value => value.DueSoonBusinessDays).HasColumnName("due_soon_business_days");
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
            // Nullable: un NULL pasa el IN (da desconocido, no falso), que es lo que quieren las entradas sin medio manual.
            table.HasCheckConstraint("CK_tickets_manual_medium", InList("manual_medium", Enum.GetNames<ManualIntakeMedium>()));
            // Fase 2 (plan E4): el canal viaja por nombre, y seguir el hilo exige conversación y ventana.
            table.HasCheckConstraint(
                "CK_tickets_conversation_channel", InList("conversation_channel", SupportConversationRef.Channels));
            table.HasCheckConstraint(
                "CK_tickets_follow", "NOT follow_thread OR (conversation_id IS NOT NULL AND follow_since IS NOT NULL)");
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
        // La conversación es una referencia blanda a messaging.conversations: otro esquema, sin FK.
        ticket.Property(value => value.ConversationId).HasColumnName("conversation_id");
        ticket.Property(value => value.ConversationChannel)
            .HasColumnName("conversation_channel")
            .HasMaxLength(SupportConversationRef.ChannelMaxLength);
        ticket.Property(value => value.FollowThread).HasColumnName("follow_thread").HasDefaultValue(false);
        ticket.Property(value => value.FollowSince).HasColumnName("follow_since");
        ticket.Property(value => value.FollowOffAt).HasColumnName("follow_off_at");
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
        // Para SupportUserReferenceProbe, que busca por membresía sola, sin tenant: un usuario se borra en todos sus tenants.
        ticket.HasIndex(value => value.AssigneeMemberId).HasDatabaseName("IX_tickets_assignee");
        // Los tickets de una conversación: los candidatos de cada entrante y la pestaña de Soporte de la ficha.
        ticket.HasIndex(value => new { value.TenantId, value.ConversationId })
            .HasFilter("conversation_id IS NOT NULL")
            .HasDatabaseName("IX_tickets_tenant_conversation");
    }

    private static void ConfigureTicketMessage(ModelBuilder modelBuilder)
    {
        var link = modelBuilder.Entity<SupportTicketMessage>();
        link.ToTable("ticket_messages", Schema, table =>
            table.HasCheckConstraint("CK_ticket_messages_state", InList("state", Enum.GetNames<SupportTicketMessageState>())));
        link.HasKey(value => value.Id);
        link.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        link.Property(value => value.TenantId).HasColumnName("tenant_id");
        link.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        // Conversación y mensaje son referencias blandas a messaging: otro esquema, sin FK.
        link.Property(value => value.ConversationId).HasColumnName("conversation_id");
        link.Property(value => value.MessageId).HasColumnName("message_id");
        link.Property(value => value.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        link.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        link.Property(value => value.CreatedAt).HasColumnName("created_at");
        link.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        link.HasOne<SupportTicket>()
            .WithMany()
            .HasForeignKey(value => value.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        link.HasIndex(value => new { value.TicketId, value.MessageId }).IsUnique().HasDatabaseName(TicketMessageIndex);
        // El expediente: del más nuevo al más viejo, sin los soltados.
        link.HasIndex(value => new { value.TicketId, value.OccurredAt, value.MessageId })
            .IsDescending(false, true, true)
            .HasFilter("state <> 'Released'")
            .HasDatabaseName("IX_ticket_messages_ticket_visible");
        // Para el borrado de clientes, que limpia los vínculos de las conversaciones que se borran (plan E18).
        link.HasIndex(value => new { value.TenantId, value.ConversationId }).HasDatabaseName("IX_ticket_messages_conversation");
    }

    private static void ConfigureUnclassifiedMessage(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<SupportUnclassifiedMessage>();
        // Nullable: un NULL pasa el IN, que es lo que quiere una fila pendiente.
        item.ToTable("unclassified_messages", Schema, table =>
            table.HasCheckConstraint(
                "CK_unclassified_messages_resolution", InList("resolution", Enum.GetNames<SupportUnclassifiedResolution>())));
        item.HasKey(value => value.MessageId).HasName(UnclassifiedMessageKey);
        item.Property(value => value.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        item.Property(value => value.TenantId).HasColumnName("tenant_id");
        item.Property(value => value.ConversationId).HasColumnName("conversation_id");
        item.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        item.Property(value => value.CreatedAt).HasColumnName("created_at");
        item.Property(value => value.Resolution).HasColumnName("resolution").HasConversion<string>().HasMaxLength(16);
        item.Property(value => value.ResolvedAt).HasColumnName("resolved_at");
        item.Property(value => value.ResolvedByMemberId).HasColumnName("resolved_by_member_id");
        // Sin FK al ticket: la fila es del mensaje, y un ticket nunca se borra.
        item.Property(value => value.AssignedTicketId)
            .HasColumnName("assigned_ticket_id")
            .HasConversion(new ValueConverter<SupportTicketId, Guid>(id => id.Value, value => new SupportTicketId(value)));
        item.Ignore(value => value.IsPending);
        item.HasIndex(value => new { value.TenantId, value.ConversationId })
            .HasFilter("resolution IS NULL")
            .HasDatabaseName("IX_unclassified_pending");
        // Para SupportUserReferenceProbe, que busca por membresía sola.
        item.HasIndex(value => value.ResolvedByMemberId).HasDatabaseName("IX_unclassified_resolved_by");
    }

    private static void ConfigureTicketReply(ModelBuilder modelBuilder)
    {
        var reply = modelBuilder.Entity<SupportTicketReply>();
        reply.ToTable("ticket_replies", Schema, table =>
        {
            table.HasCheckConstraint("CK_ticket_replies_channel", InList("channel", Enum.GetNames<SupportReplyChannel>()));
            table.HasCheckConstraint("CK_ticket_replies_delivery", InList("delivery", Enum.GetNames<SupportReplyDelivery>()));
            table.HasCheckConstraint("CK_ticket_replies_status", InList("status", Enum.GetNames<SupportReplyStatus>()));
        });
        reply.HasKey(value => value.Id);
        reply.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        reply.Property(value => value.TenantId).HasColumnName("tenant_id");
        reply.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        reply.Property(value => value.ClientId).HasColumnName("client_id");
        reply.Property(value => value.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(24);
        reply.Property(value => value.Delivery).HasColumnName("delivery").HasConversion<string>().HasMaxLength(16);
        reply.Property(value => value.Text).HasColumnName("text").HasMaxLength(SupportTicketReply.TextMaxLength);
        reply.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        reply.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(SupportTicketReply.FailureCodeMaxLength);
        // Mensaje y conexión son referencias blandas a messaging e integrations.
        reply.Property(value => value.MessageId).HasColumnName("message_id");
        reply.Property(value => value.ConnectionId).HasColumnName("connection_id");
        reply.Property(value => value.AuthorMemberId).HasColumnName("author_member_id");
        reply.Property(value => value.CreatedAt).HasColumnName("created_at");
        reply.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        reply.HasOne<SupportTicket>()
            .WithMany()
            .HasForeignKey(value => value.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        reply.HasIndex(value => new { value.TicketId, value.ClientId }).IsUnique().HasDatabaseName(TicketReplyClientIndex);
        // Para SupportUserReferenceProbe, que busca por membresía sola.
        reply.HasIndex(value => value.AuthorMemberId).HasDatabaseName("IX_ticket_replies_author");
    }

    private static void ConfigureTicketEvent(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<SupportTicketEvent>();
        item.ToTable("ticket_events", Schema, table =>
            table.HasCheckConstraint("CK_ticket_events_kind", InList("kind", Enum.GetNames<SupportTicketEventKind>())));
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
        // Para SupportUserReferenceProbe, igual que IX_tickets_assignee: la traza es la tabla que más crece.
        item.HasIndex(value => value.ActorMemberId).HasDatabaseName("IX_ticket_events_actor");
    }

    private static string InList(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(',', values.Select(value => $"'{value}'"))})";
}
