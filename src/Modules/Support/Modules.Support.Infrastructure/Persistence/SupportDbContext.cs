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

    /// <summary>Los vínculos por (tenant, conversación, mensaje): el NOT EXISTS de <c>SupportThreadFeed</c> lo nombra en su comentario.</summary>
    public const string TicketMessageConversationIndex = "IX_ticket_messages_conversation";

    public DbSet<SupportTicketType> TicketTypes => Set<SupportTicketType>();

    public DbSet<SupportTenantSettings> TenantSettings => Set<SupportTenantSettings>();

    public DbSet<SupportTicketMessage> TicketMessages => Set<SupportTicketMessage>();

    public DbSet<SupportUnclassifiedMessage> UnclassifiedMessages => Set<SupportUnclassifiedMessage>();

    public DbSet<SupportTicketReply> TicketReplies => Set<SupportTicketReply>();

    /// <summary>Único de la versión del formulario por tenant (plan F14): dos guardados de la misma versión chocan acá.</summary>
    public const string FormVersionIndex = "IX_form_versions_tenant_version";

    /// <summary>Único de la versión de los textos legales por tenant (plan F16).</summary>
    public const string LegalTextVersionIndex = "IX_legal_text_versions_tenant_version";

    public DbSet<SupportTicketAccess> TicketAccess => Set<SupportTicketAccess>();

    public DbSet<SupportFormVersion> FormVersions => Set<SupportFormVersion>();

    public DbSet<SupportLegalTextVersion> LegalTextVersions => Set<SupportLegalTextVersion>();

    public DbSet<SupportConsent> Consents => Set<SupportConsent>();

    /// <summary>Único del archivo de Storage (plan F35): dos radicaciones que reclaman el mismo archivo chocan acá.</summary>
    public const string TicketAttachmentFileIndex = "IX_ticket_attachments_file";

    public DbSet<SupportTicketAttachment> TicketAttachments => Set<SupportTicketAttachment>();

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
        ConfigureTicketAccess(modelBuilder);
        ConfigureFormVersion(modelBuilder);
        ConfigureLegalTextVersion(modelBuilder);
        ConfigureConsent(modelBuilder);
        ConfigureTicketAttachment(modelBuilder);
    }

    // Plan F35: el archivo es una referencia blanda a storage.file_resources (otro esquema, sin FK); el ticket sí, con cascada. El
    // único sobre file_id lo usan la radicación (traducido por nombre) y la sonda de Storage (SupportAttachmentReferenceProbe).
    private static void ConfigureTicketAttachment(ModelBuilder modelBuilder)
    {
        var attachment = modelBuilder.Entity<SupportTicketAttachment>();
        attachment.ToTable("ticket_attachments", Schema, table =>
            table.HasCheckConstraint("CK_ticket_attachments_size", "size_bytes > 0"));
        attachment.HasKey(value => value.Id);
        attachment.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        attachment.Property(value => value.TenantId).HasColumnName("tenant_id");
        attachment.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        attachment.Property(value => value.FileId).HasColumnName("file_id");
        attachment.Property(value => value.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(SupportPublicAttachmentRules.NameMaxLength);
        attachment.Property(value => value.MimeType)
            .HasColumnName("mime_type")
            .HasMaxLength(SupportPublicAttachmentRules.MimeTypeMaxLength);
        attachment.Property(value => value.SizeBytes).HasColumnName("size_bytes");
        attachment.Property(value => value.CreatedAt).HasColumnName("created_at");
        attachment.HasOne<SupportTicket>()
            .WithMany()
            .HasForeignKey(value => value.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        attachment.HasIndex(value => value.FileId).IsUnique().HasDatabaseName(TicketAttachmentFileIndex);
        attachment.HasIndex(value => new { value.TenantId, value.TicketId }).HasDatabaseName("IX_ticket_attachments_tenant_ticket");
    }

    // Fase 3 (plan F7): la clave de consulta vive aparte del ticket para que un intento fallido no le suba la versión.
    private static void ConfigureTicketAccess(ModelBuilder modelBuilder)
    {
        var access = modelBuilder.Entity<SupportTicketAccess>();
        access.ToTable("ticket_access", Schema, table =>
            table.HasCheckConstraint("CK_ticket_access_failed_attempts", "failed_attempts >= 0"));
        access.HasKey(value => value.TicketId);
        access.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value))
            .ValueGeneratedNever();
        access.Property(value => value.TenantId).HasColumnName("tenant_id");
        access.Property(value => value.PinHash).HasColumnName("pin_hash").HasMaxLength(SupportTicketAccess.PinHashMaxLength);
        access.Property(value => value.FailedAttempts).HasColumnName("failed_attempts");
        access.Property(value => value.LockedUntil).HasColumnName("locked_until");
        access.Property(value => value.PinIssuedAt).HasColumnName("pin_issued_at");
        access.Property(value => value.LastSentAt).HasColumnName("last_sent_at");
        access.Property(value => value.LastSentChannel)
            .HasColumnName("last_sent_channel")
            .HasMaxLength(SupportTicketAccess.ChannelMaxLength);
        access.Property(value => value.LastFailureCode)
            .HasColumnName("last_failure_code")
            .HasMaxLength(SupportTicketAccess.FailureCodeMaxLength);
        access.HasOne<SupportTicket>()
            .WithOne()
            .HasForeignKey<SupportTicketAccess>(value => value.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    // Insert-only (plan F14): ninguna versión se edita. created_by es una membresía y entra en SupportUserReferenceProbe.
    private static void ConfigureFormVersion(ModelBuilder modelBuilder)
    {
        var form = modelBuilder.Entity<SupportFormVersion>();
        form.ToTable("form_versions", Schema, table =>
            table.HasCheckConstraint("CK_form_versions_version", "version >= 1"));
        form.HasKey(value => value.Id);
        form.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        form.Property(value => value.TenantId).HasColumnName("tenant_id");
        form.Property(value => value.Version).HasColumnName("version");
        form.Property(value => value.IsPublished).HasColumnName("is_published");
        form.Property(value => value.ShowDocument).HasColumnName("show_document");
        form.Property(value => value.ShowAddress).HasColumnName("show_address");
        form.Property(value => value.ShowLandline).HasColumnName("show_landline");
        form.Property(value => value.BusinessNameRequired).HasColumnName("business_name_required");
        form.Property(value => value.AllowAttachments).HasColumnName("allow_attachments").HasDefaultValue(false);
        form.Property(value => value.AccentColor).HasColumnName("accent_color").HasMaxLength(SupportFormVersion.AccentColorLength);
        form.Property(value => value.IntroText).HasColumnName("intro_text").HasMaxLength(SupportFormVersion.IntroTextMaxLength);
        form.Property(value => value.SuccessMessage)
            .HasColumnName("success_message")
            .HasMaxLength(SupportFormVersion.SuccessMessageMaxLength);
        form.Property(value => value.CreatedAt).HasColumnName("created_at");
        form.Property(value => value.CreatedBy).HasColumnName("created_by");
        form.HasIndex(value => new { value.TenantId, value.Version }).IsUnique().HasDatabaseName(FormVersionIndex);
        // Para SupportUserReferenceProbe, que busca por membresía sola.
        form.HasIndex(value => value.CreatedBy).HasDatabaseName("IX_form_versions_created_by");
    }

    // Prueba legal, insert-only (plan F16, spec §9.5): ninguna versión se edita ni se borra, y el Down de la migración no la toca.
    private static void ConfigureLegalTextVersion(ModelBuilder modelBuilder)
    {
        var text = modelBuilder.Entity<SupportLegalTextVersion>();
        text.ToTable("legal_text_versions", Schema, table =>
        {
            table.HasCheckConstraint("CK_legal_text_versions_version", "version >= 1");
            table.HasCheckConstraint("CK_legal_text_versions_policy", "policy_text IS NOT NULL OR policy_url IS NOT NULL");
        });
        text.HasKey(value => value.Id);
        text.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        text.Property(value => value.TenantId).HasColumnName("tenant_id");
        text.Property(value => value.Version).HasColumnName("version");
        text.Property(value => value.AuthorizationText)
            .HasColumnName("authorization_text")
            .HasMaxLength(SupportLegalTextVersion.TextMaxLength);
        text.Property(value => value.PolicyText).HasColumnName("policy_text").HasMaxLength(SupportLegalTextVersion.TextMaxLength);
        text.Property(value => value.PolicyUrl).HasColumnName("policy_url").HasMaxLength(SupportLegalTextVersion.PolicyUrlMaxLength);
        text.Property(value => value.TermsText).HasColumnName("terms_text").HasMaxLength(SupportLegalTextVersion.TextMaxLength);
        text.Property(value => value.CreatedAt).HasColumnName("created_at");
        text.Property(value => value.CreatedBy).HasColumnName("created_by");
        text.Ignore(value => value.IsExample);
        text.HasIndex(value => new { value.TenantId, value.Version }).IsUnique().HasDatabaseName(LegalTextVersionIndex);
        // Para SupportUserReferenceProbe, que busca por membresía sola.
        text.HasIndex(value => value.CreatedBy).HasDatabaseName("IX_legal_text_versions_created_by");
    }

    // Prueba legal, insert-only (plan F17): sin FK a propósito, ni al ticket ni a la versión, para que nada la arrastre al borrar; la
    // fila guarda su propia foto del texto. El Down de la migración no la borra (precedente customers.data_consents).
    private static void ConfigureConsent(ModelBuilder modelBuilder)
    {
        var consent = modelBuilder.Entity<SupportConsent>();
        consent.ToTable("consents", Schema);
        consent.HasKey(value => value.Id);
        consent.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        consent.Property(value => value.TenantId).HasColumnName("tenant_id");
        consent.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        consent.Property(value => value.LegalTextVersionId).HasColumnName("legal_text_version_id");
        consent.Property(value => value.LegalTextVersion).HasColumnName("legal_text_version");
        consent.Property(value => value.AcceptedAt).HasColumnName("accepted_at");
        consent.Property(value => value.AuthorizationText)
            .HasColumnName("authorization_text")
            .HasMaxLength(SupportLegalTextVersion.TextMaxLength);
        consent.Property(value => value.PolicyText).HasColumnName("policy_text").HasMaxLength(SupportLegalTextVersion.TextMaxLength);
        consent.Property(value => value.PolicyUrl).HasColumnName("policy_url").HasMaxLength(SupportLegalTextVersion.PolicyUrlMaxLength);
        consent.Property(value => value.TermsText).HasColumnName("terms_text").HasMaxLength(SupportLegalTextVersion.TextMaxLength);
        consent.Property(value => value.VisitorIp).HasColumnName("visitor_ip").HasMaxLength(SupportConsent.VisitorIpMaxLength);
        // Una fila por ticket.
        consent.HasIndex(value => value.TicketId).IsUnique().HasDatabaseName("IX_consents_ticket");
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
            // Fase 3 (plan F19): la ciudad de Geography con país CO (referencia blanda, otro esquema) o el nombre libre fuera.
            requester.Property(value => value.CityId).HasColumnName("requester_city_id");
            requester.Property(value => value.CityName)
                .HasColumnName("requester_city_name")
                .HasMaxLength(TicketRequester.CityNameMaxLength);
        });
        // Fase 3 (plan F22): el anónimo del formulario público y la versión del formulario con que se radicó (sin FK: las versiones
        // nunca se borran).
        ticket.Property(value => value.IsAnonymous).HasColumnName("is_anonymous").HasDefaultValue(false);
        ticket.Property(value => value.FormVersionId).HasColumnName("form_version_id");
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
        // Para el borrado de clientes, que limpia los vínculos de las conversaciones que se borran (plan E18), y para el NOT EXISTS del
        // feed del seguimiento, que busca por (tenant, conversación, mensaje): con message_id en el índice no recorre los vínculos de
        // toda la conversación por cada entrante (revisión final, I2).
        link.HasIndex(value => new { value.TenantId, value.ConversationId, value.MessageId })
            .HasDatabaseName(TicketMessageConversationIndex);
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
        // Lo que retoma una Pending vieja (revisión final, I1 y M2): el destino de la plantilla al celular y el adjunto del hilo.
        reply.Property(value => value.TargetWaId).HasColumnName("target_wa_id").HasMaxLength(SupportTicketReply.TargetWaIdMaxLength);
        reply.Property(value => value.UploadId).HasColumnName("upload_id");
        reply.Property(value => value.FileName).HasColumnName("file_name").HasMaxLength(SupportTicketReply.FileNameMaxLength);
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
