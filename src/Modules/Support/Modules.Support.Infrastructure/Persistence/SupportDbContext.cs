using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

    /// <summary>Spec 2026-11-22: único por tenant donde <c>is_internal</c>; un tenant tiene un solo tipo interno. SQL a mano en
    /// <c>AddSupportInternalTasks</c>, como el de nombre.</summary>
    public const string InternalTypeIndex = "UX_ticket_types_tenant_internal";

    /// <summary>Spec 2026-11-22: una tarea no tiene cliente, conversación, medio, solicitante ni canal de respuesta, y sólo ella tiene el
    /// canal <c>None</c>. El dominio lo exige; el CHECK lo respalda contra el SQL crudo.</summary>
    public const string TicketInternalCheck = "CK_tickets_internal";

    /// <summary>Único del ordinal de la traza: dos guardados que agregan el mismo ordinal chocan acá.</summary>
    public const string TicketEventOrdinalIndex = "IX_ticket_events_ticket_ordinal";

    /// <summary>Fase 4a (plan G5): <c>tickets.ticket_type_id → ticket_types.id</c> con <c>ON DELETE RESTRICT</c>. Del lado del tipo
    /// (borrar uno usado) da <c>23001</c>; del lado del ticket (radicar o cambiar a un tipo recién borrado), <c>23503</c>.</summary>
    public const string TicketTypeForeignKey = "FK_tickets_ticket_types_ticket_type_id";

    /// <summary>Fase 4b (H15): una encuesta por <c>(ticket, número de resolución)</c>. Inalcanzable después de la versión del ticket;
    /// se traduce igual, como red.</summary>
    public const string TicketSurveyResolutionIndex = "UX_ticket_surveys_ticket_resolution";

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

    /// <summary>Fase 4b (H9): el PDF de la respuesta formal. Insert-only: es prueba legal de lo que se envió. Su único borrado es la
    /// cascada de una respuesta que nunca salió (<c>SendTicketReplyHandler</c> quita la que el canal rechazó antes de enviar).</summary>
    public DbSet<SupportReplyDocument> ReplyDocuments => Set<SupportReplyDocument>();

    /// <summary>Fase 4b (H15 a H21): la encuesta de cada ciclo de resolución. Es una cola con timbre
    /// (<c>QueueDoorbellInterceptor</c>), no prueba legal: se actualiza.</summary>
    public DbSet<SupportTicketSurvey> TicketSurveys => Set<SupportTicketSurvey>();

    /// <summary>Único de la versión del formulario por tenant (plan F14): dos guardados de la misma versión chocan acá.</summary>
    public const string FormVersionIndex = "IX_form_versions_tenant_version";

    /// <summary>Único de la versión de los textos legales por tenant (plan F16).</summary>
    public const string LegalTextVersionIndex = "IX_legal_text_versions_tenant_version";

    /// <summary>Único del consentimiento por ticket (plan F17): una fila por radicado. Se traduce por nombre (triage #2 del Bloque F).</summary>
    public const string ConsentTicketIndex = "IX_consents_ticket";

    /// <summary>
    /// Triage #1 del Bloque F: un anónimo no tiene cliente ni ninguna columna del solicitante. El dominio ya lo exige
    /// (<c>CreateFromPublicForm</c> y <c>LinkCustomer</c>); el CHECK lo respalda contra el SQL crudo (fusión, siembras, un arreglo a mano).
    /// </summary>
    public const string TicketAnonymousCheck = "CK_tickets_anonymous";

    // Las columnas de la foto del solicitante (ConfigureTicket): una nueva entra acá, o el CHECK deja de cubrirla.
    private static readonly string[] RequesterColumns =
    [
        "requester_first_names", "requester_last_names", "requester_business_name", "requester_document_type",
        "requester_document_number", "requester_email", "requester_mobile_phone", "requester_landline_phone", "requester_country_code",
        "requester_address", "requester_city_id", "requester_city_name",
    ];

    /// <summary>Unique idempotency key of the public filing per tenant (spec 2026-11-15 §9.2, plan L27). Translated by name in
    /// <c>SupportUnitOfWork</c> to <c>SupportClientRequestTakenException</c>.</summary>
    public const string TicketClientRequestIndex = "IX_tickets_tenant_client_request";

    public DbSet<SupportTicketAccess> TicketAccess => Set<SupportTicketAccess>();

    /// <summary>Spec 2026-11-15 §9.3 (plan L29): the FK to <c>companies.companies</c> lives only in <c>AddSupportFormOrganization</c>,
    /// written by hand (EF does not model relations across module contexts; precedent <c>pos.cash_sessions</c>).</summary>
    public const string FormOrganizationForeignKey = "FK_form_versions_companies_organization_company_id";

    public DbSet<SupportFormVersion> FormVersions => Set<SupportFormVersion>();

    public DbSet<SupportLegalTextVersion> LegalTextVersions => Set<SupportLegalTextVersion>();

    public DbSet<SupportConsent> Consents => Set<SupportConsent>();

    /// <summary>Unique version per tenant and legal document kind (spec 2026-11-15 §5.1): two publications of the same one collide here.</summary>
    public const string LegalDocumentVersionIndex = "IX_legal_document_versions_tenant_kind_version";

    /// <summary>One acceptance per ticket and kind (spec 2026-11-15 §5.2). Translated by name in <c>SupportUnitOfWork</c>.</summary>
    public const string LegalAcceptanceTicketKindIndex = "IX_legal_acceptances_ticket_kind";

    public DbSet<SupportLegalDocumentVersion> LegalDocumentVersions => Set<SupportLegalDocumentVersion>();

    public DbSet<SupportLegalAcceptance> LegalAcceptances => Set<SupportLegalAcceptance>();

    /// <summary>Único del archivo de Storage (plan F35): dos radicaciones que reclaman el mismo archivo chocan acá.</summary>
    public const string TicketAttachmentFileIndex = "IX_ticket_attachments_file";

    public DbSet<SupportTicketAttachment> TicketAttachments => Set<SupportTicketAttachment>();

    /// <summary>The note's PK is the screen's clientId (plan N15): two requests with the same one race here, and the loser gets 409.</summary>
    public const string TicketNoteKey = "PK_ticket_notes";

    /// <summary>Unique Storage file of a note (spec 2026-11-15 §5.4): one file never sits in two notes.</summary>
    public const string TicketNoteFileIndex = "IX_ticket_note_files_file";

    public DbSet<SupportTicketNote> TicketNotes => Set<SupportTicketNote>();

    // Spec 2026-11-22 D-16: the claim of each unassigned alert. The worker writes it with SQL; the DbSet exists so the model owns the table.
    internal DbSet<SupportUnassignedAlertClaim> UnassignedAlerts => Set<SupportUnassignedAlertClaim>();

    public DbSet<SupportTicketNoteFile> TicketNoteFiles => Set<SupportTicketNoteFile>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // Proyección de escritura de platform.outbox_messages (ExcludeFromMigrations): Tenancy es dueña de la tabla. Spec 2026-11-22 §12.
    internal DbSet<SupportOutboxMessage> Outbox => Set<SupportOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureOutboxProjection(modelBuilder);
        ConfigureSettings(modelBuilder);
        ConfigureTicketType(modelBuilder);
        ConfigureTicket(modelBuilder);
        ConfigureTicketEvent(modelBuilder);
        ConfigureTicketMessage(modelBuilder);
        ConfigureUnclassifiedMessage(modelBuilder);
        ConfigureTicketReply(modelBuilder);
        ConfigureReplyDocument(modelBuilder);
        ConfigureTicketSurvey(modelBuilder);
        ConfigureTicketAccess(modelBuilder);
        ConfigureFormVersion(modelBuilder);
        ConfigureLegalTextVersion(modelBuilder);
        ConfigureConsent(modelBuilder);
        ConfigureLegalDocumentVersion(modelBuilder);
        ConfigureLegalAcceptance(modelBuilder);
        ConfigureTicketAttachment(modelBuilder);
        ConfigureTicketNote(modelBuilder);
        ConfigureUnassignedAlert(modelBuilder);
    }

    // Spec 2026-11-22 D-16. No foreign key to the ticket: the claim is bookkeeping about a stretch, and nothing deletes tickets today.
    private static void ConfigureUnassignedAlert(ModelBuilder modelBuilder)
    {
        var claim = modelBuilder.Entity<SupportUnassignedAlertClaim>();
        claim.ToTable("unassigned_alerts", Schema);
        claim.HasKey(value => new { value.TicketId, value.UnassignedSince }).HasName("PK_unassigned_alerts");
        claim.Property(value => value.TicketId).HasColumnName("ticket_id").ValueGeneratedNever();
        claim.Property(value => value.UnassignedSince).HasColumnName("unassigned_since").ValueGeneratedNever();
        claim.Property(value => value.TenantId).HasColumnName("tenant_id");
        claim.Property(value => value.RecipientCount).HasColumnName("recipient_count");
        claim.Property(value => value.AlertedAt).HasColumnName("alerted_at");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<SupportOutboxMessage>();
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

    // Spec 2026-11-15 §5.4: two insert-only tables. The ticket cascades like the other ticket tables; the author is a soft reference
    // to tenancy, listed in SupportUserReferenceProbe (plan N23). The files keep the snapshot of name, type and size (plan N17) and a
    // unique file_id that the Storage probe and CountPendingAsync use.
    private static void ConfigureTicketNote(ModelBuilder modelBuilder)
    {
        var note = modelBuilder.Entity<SupportTicketNote>();
        note.ToTable("ticket_notes", Schema);
        note.HasKey(value => value.Id).HasName(TicketNoteKey);
        note.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        note.Property(value => value.TenantId).HasColumnName("tenant_id");
        note.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        note.Property(value => value.AuthorMemberId).HasColumnName("author_member_id");
        note.Property(value => value.Text).HasColumnName("text").HasMaxLength(SupportTicketReply.TextMaxLength);
        note.Property(value => value.CreatedAt).HasColumnName("created_at");
        note.HasOne<SupportTicket>()
            .WithMany()
            .HasForeignKey(value => value.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
        note.HasMany(value => value.Files)
            .WithOne()
            .HasForeignKey(file => file.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
        note.Navigation(value => value.Files).UsePropertyAccessMode(PropertyAccessMode.Field);
        note.HasIndex(value => new { value.TenantId, value.TicketId, value.CreatedAt, value.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_ticket_notes_tenant_ticket_created");
        // For SupportUserReferenceProbe, which searches by membership alone.
        note.HasIndex(value => value.AuthorMemberId).HasDatabaseName("IX_ticket_notes_author");

        var file = modelBuilder.Entity<SupportTicketNoteFile>();
        file.ToTable("ticket_note_files", Schema, table =>
        {
            table.HasCheckConstraint("CK_ticket_note_files_size", "size_bytes > 0");
            table.HasCheckConstraint("CK_ticket_note_files_position", $"position BETWEEN 0 AND {SupportTicketNote.MaxFiles - 1}");
        });
        file.HasKey(value => new { value.NoteId, value.FileId });
        file.Property(value => value.NoteId).HasColumnName("note_id");
        file.Property(value => value.FileId).HasColumnName("file_id");
        file.Property(value => value.Position).HasColumnName("position");
        file.Property(value => value.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(SupportPublicAttachmentRules.NameMaxLength);
        file.Property(value => value.MimeType)
            .HasColumnName("mime_type")
            .HasMaxLength(SupportPublicAttachmentRules.MimeTypeMaxLength);
        file.Property(value => value.SizeBytes).HasColumnName("size_bytes");
        file.HasIndex(value => value.FileId).IsUnique().HasDatabaseName(TicketNoteFileIndex);
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
        // Owner, 2026-11-15 (E2): prendido por defecto. El sentinel en true (precedente: Store, show_prices) hace que EF mande siempre un
        // false: sin él, un bool con default true se omite del INSERT cuando vale false y la base lo dejaría en true.
        form.Property(value => value.AllowAttachments).HasColumnName("allow_attachments").HasDefaultValue(true).HasSentinel(true);
        form.Property(value => value.AccentColor).HasColumnName("accent_color").HasMaxLength(SupportFormVersion.AccentColorLength);
        form.Property(value => value.IntroText).HasColumnName("intro_text").HasMaxLength(SupportFormVersion.IntroTextMaxLength);
        form.Property(value => value.SuccessMessage)
            .HasColumnName("success_message")
            .HasMaxLength(SupportFormVersion.SuccessMessageMaxLength);
        // Spec 2026-11-15 §9.3 (plan L29): the company that answers. The column and its index are modeled here; the FK to
        // companies.companies (ON DELETE RESTRICT) only in the migration (FormOrganizationForeignKey).
        form.Property(value => value.OrganizationCompanyId).HasColumnName("organization_company_id");
        form.Property(value => value.CreatedAt).HasColumnName("created_at");
        form.Property(value => value.CreatedBy).HasColumnName("created_by");
        form.HasIndex(value => new { value.TenantId, value.Version }).IsUnique().HasDatabaseName(FormVersionIndex);
        // Serves the RESTRICT check of a company DELETE (PostgreSQL does not index the referencing side by itself).
        form.HasIndex(value => value.OrganizationCompanyId).HasDatabaseName("IX_form_versions_organization_company");
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
        consent.HasIndex(value => value.TicketId).IsUnique().HasDatabaseName(ConsentTicketIndex);
    }

    // Spec 2026-11-15 §5.1: legal evidence, insert-only. No FK; the Down of AddSupportLegalDocuments keeps the table. The kind is
    // stored by name; the question and the URL belong only to the privacy notice. created_by is listed in SupportUserReferenceProbe.
    private static void ConfigureLegalDocumentVersion(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<SupportLegalDocumentVersion>();
        document.ToTable("legal_document_versions", Schema, table =>
        {
            table.HasCheckConstraint(
                "CK_legal_document_versions_kind", "kind IN ('PrivacyNotice', 'AnonymousNotice', 'Terms')");
            table.HasCheckConstraint("CK_legal_document_versions_version", "version >= 1");
            table.HasCheckConstraint(
                "CK_legal_document_versions_privacy_only",
                "kind = 'PrivacyNotice' OR (authorization_question IS NULL AND policy_url IS NULL)");
        });
        document.HasKey(value => value.Id);
        document.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        document.Property(value => value.TenantId).HasColumnName("tenant_id");
        document.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        document.Property(value => value.Version).HasColumnName("version");
        document.Property(value => value.ContentHtml)
            .HasColumnName("content_html")
            .HasMaxLength(SupportLegalDocumentVersion.ContentMaxLength);
        document.Property(value => value.AuthorizationQuestion)
            .HasColumnName("authorization_question")
            .HasMaxLength(SupportLegalDocumentVersion.AuthorizationQuestionMaxLength);
        document.Property(value => value.PolicyUrl)
            .HasColumnName("policy_url")
            .HasMaxLength(SupportLegalDocumentVersion.PolicyUrlMaxLength);
        document.Property(value => value.CreatedAt).HasColumnName("created_at");
        document.Property(value => value.CreatedBy).HasColumnName("created_by");
        document.Ignore(value => value.IsExample);
        document.HasIndex(value => new { value.TenantId, value.Kind, value.Version })
            .IsUnique()
            .HasDatabaseName(LegalDocumentVersionIndex);
        // For SupportUserReferenceProbe, which looks up by membership alone.
        document.HasIndex(value => value.CreatedBy).HasDatabaseName("IX_legal_document_versions_created_by");
    }

    // Spec 2026-11-15 §5.2: legal evidence, insert-only, the mirror of consents. No FK on purpose, neither to the ticket nor to the
    // version, so nothing drags it away; the row keeps its own snapshot. The IP check backs the domain against raw SQL: a privacy
    // notice always has an IP and an anonymous notice never does (a Terms row of an anonymous filing is kept without one by the handler).
    private static void ConfigureLegalAcceptance(ModelBuilder modelBuilder)
    {
        var acceptance = modelBuilder.Entity<SupportLegalAcceptance>();
        acceptance.ToTable("legal_acceptances", Schema, table =>
        {
            table.HasCheckConstraint("CK_legal_acceptances_kind", "kind IN ('PrivacyNotice', 'AnonymousNotice', 'Terms')");
            table.HasCheckConstraint(
                "CK_legal_acceptances_ip",
                "(kind <> 'PrivacyNotice' OR visitor_ip IS NOT NULL) AND (kind <> 'AnonymousNotice' OR visitor_ip IS NULL)");
        });
        acceptance.HasKey(value => value.Id);
        acceptance.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        acceptance.Property(value => value.TenantId).HasColumnName("tenant_id");
        acceptance.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        acceptance.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        acceptance.Property(value => value.DocumentVersionId).HasColumnName("document_version_id");
        acceptance.Property(value => value.DocumentVersion).HasColumnName("document_version");
        acceptance.Property(value => value.AcceptedAt).HasColumnName("accepted_at");
        acceptance.Property(value => value.ContentHtml)
            .HasColumnName("content_html")
            .HasMaxLength(SupportLegalDocumentVersion.ContentMaxLength);
        acceptance.Property(value => value.AuthorizationQuestion)
            .HasColumnName("authorization_question")
            .HasMaxLength(SupportLegalDocumentVersion.AuthorizationQuestionMaxLength);
        acceptance.Property(value => value.PolicyUrl)
            .HasColumnName("policy_url")
            .HasMaxLength(SupportLegalDocumentVersion.PolicyUrlMaxLength);
        acceptance.Property(value => value.CharacterizationText)
            .HasColumnName("characterization_text")
            .HasMaxLength(SupportLegalAcceptance.CharacterizationTextMaxLength);
        acceptance.Property(value => value.VisitorIp)
            .HasColumnName("visitor_ip")
            .HasMaxLength(SupportLegalAcceptance.VisitorIpMaxLength);
        acceptance.HasIndex(value => new { value.TicketId, value.Kind })
            .IsUnique()
            .HasDatabaseName(LegalAcceptanceTicketKindIndex);
    }

    // La fila la inserta SupportProvisioner con SQL crudo; EF sólo la lee y actualiza el umbral. El contador y created_at son
    // propiedades sombra: existen en el modelo para que la migración las cree, pero el dominio no las ve y EF nunca las escribe.
    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<SupportTenantSettings>();
        settings.ToTable("tenant_settings", Schema, table =>
        {
            table.HasCheckConstraint(
                "CK_tenant_settings_due_soon",
                $"due_soon_business_days BETWEEN {SupportTenantSettings.MinDueSoonBusinessDays} AND {SupportTenantSettings.MaxDueSoonBusinessDays}");
            table.HasCheckConstraint(
                "CK_tenant_settings_auto_close",
                $"auto_close_business_days BETWEEN {SupportTenantSettings.MinAutoCloseBusinessDays} AND {SupportTenantSettings.MaxAutoCloseBusinessDays}");
        });
        settings.HasKey(value => value.TenantId);
        settings.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        settings.Property<long>("NextTicketNumber").HasColumnName("next_ticket_number").HasDefaultValue(1L);
        // Sin HasDefaultValue en el modelo: con él EF tomaría el 0 de C# como «sin valor» y escribiría el default, y 0 es un umbral
        // válido (apaga «por vencer»). El DEFAULT 3 de la columna sí existe (migración InitialSupport): lo usa el INSERT del
        // aprovisionador.
        settings.Property(value => value.DueSoonBusinessDays).HasColumnName("due_soon_business_days");
        // Fase 4b (H6). Nullable y sin default: el INSERT del aprovisionador no la nombra y nace en NULL.
        settings.Property(value => value.FormalNextStep)
            .HasColumnName("formal_next_step")
            .HasMaxLength(SupportTenantSettings.FormalNextStepMaxLength);
        // Fase 4b (H14). Con default en la columna, para las filas que ya existen y para el INSERT del aprovisionador, que no las nombra.
        // EF nunca inserta esta fila, así que el sentinel no importa: CreateDefault ya pone los mismos valores.
        settings.Property(value => value.SurveyEnabled).HasColumnName("survey_enabled").HasDefaultValue(false);
        settings.Property(value => value.AutoCloseBusinessDays)
            .HasColumnName("auto_close_business_days")
            .HasDefaultValue(SupportTenantSettings.DefaultAutoCloseBusinessDays);
        // Spec 2026-11-22 §7 and §11. All with a default in the column (the provisioner's INSERT does not name them, and the rows that
        // exist already take them): expand-only. No foreign key on the schedule: it is data of Messaging, and one that was deleted is
        // read as «missing» and not as an error. The recipients are a uuid[] in the order they were chosen.
        settings.Property(value => value.UnassignedAlertEnabled).HasColumnName("unassigned_alert_enabled").HasDefaultValue(false);
        settings.Property(value => value.UnassignedAlertHours)
            .HasColumnName("unassigned_alert_hours")
            .HasDefaultValue(SupportUnassignedAlertSettings.DefaultThresholdHours);
        settings.Property(value => value.UnassignedAlertRecipientIds)
            .HasColumnName("unassigned_alert_recipient_ids")
            .HasColumnType("uuid[]")
            .HasDefaultValueSql("'{}'::uuid[]");
        settings.Property(value => value.UnassignedAlertScheduleId).HasColumnName("unassigned_alert_schedule_id");
        settings.Property(value => value.UnassignedAlertEnabledAt).HasColumnName("unassigned_alert_enabled_at");
        // Task-notices spec 2026-11-22 §5.4. Nullable and without default (the provisioner INSERT does not name it): expand-only. No foreign
        // key: the connection is Integrations data, and one that was paused or deleted is read as «not listed», never as an error.
        settings.Property(value => value.TaskNoticeConnectionId).HasColumnName("task_notice_connection_id");
        // A read of the four columns above as one value, not a column.
        settings.Ignore(value => value.UnassignedAlert);
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
            // Spec 2026-11-22: el tipo interno no tiene término, no se publica y siempre está activo.
            table.HasCheckConstraint(
                "CK_ticket_types_internal",
                "NOT is_internal OR (term_business_days IS NULL AND NOT term_is_legal AND NOT requires_formal_response"
                + " AND NOT is_published AND is_active)");
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
        // Fase 4b (H6). Nullable: la siembra del aprovisionador no la nombra y nace en NULL.
        type.Property(value => value.FormalNextStep)
            .HasColumnName("formal_next_step")
            .HasMaxLength(SupportTicketType.FormalNextStepMaxLength);
        type.Property(value => value.IsPublished).HasColumnName("is_published");
        type.Property(value => value.IsActive).HasColumnName("is_active");
        type.Property(value => value.IsInternal).HasColumnName("is_internal").HasDefaultValue(false);
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
            table.HasCheckConstraint("CK_tickets_priority", InList("priority", Enum.GetNames<SupportTicketPriority>()));
            table.HasCheckConstraint("CK_tickets_intake", InList("intake", Enum.GetNames<SupportTicketIntake>()));
            table.HasCheckConstraint("CK_tickets_reply_channel", InList("reply_channel", Enum.GetNames<SupportReplyChannel>()));
            // Nullable: un NULL pasa el IN (da desconocido, no falso), que es lo que quieren las entradas sin medio manual.
            table.HasCheckConstraint("CK_tickets_manual_medium", InList("manual_medium", Enum.GetNames<ManualIntakeMedium>()));
            // Fase 2 (plan E4): el canal viaja por nombre, y seguir el hilo exige conversación y ventana.
            table.HasCheckConstraint(
                "CK_tickets_conversation_channel", InList("conversation_channel", SupportConversationRef.Channels));
            table.HasCheckConstraint(
                "CK_tickets_follow", "NOT follow_thread OR (conversation_id IS NOT NULL AND follow_since IS NOT NULL)");
            // Fase 4a (plan G9): la prórroga se guarda entera o no se guarda; sin prórroga los días quedan en 0.
            table.HasCheckConstraint(
                "CK_tickets_extension",
                "(extended_at IS NULL) = (extension_reason IS NULL) AND extension_business_days >= 0"
                + " AND (extended_at IS NULL) = (extension_business_days = 0)");
            table.HasCheckConstraint(
                TicketAnonymousCheck,
                $"NOT is_anonymous OR (customer_id IS NULL AND {string.Join(" AND ", RequesterColumns.Select(column => $"{column} IS NULL"))})");
            // Spec 2026-11-22: una tarea no tiene cliente, conversación, medio, solicitante ni anonimato, y el canal None es de ella.
            table.HasCheckConstraint(
                TicketInternalCheck,
                $"(NOT is_internal OR (customer_id IS NULL AND conversation_id IS NULL AND manual_medium IS NULL AND NOT is_anonymous AND {string.Join(" AND ", RequesterColumns.Select(column => $"{column} IS NULL"))}))"
                + " AND ((reply_channel = 'None') = is_internal)");
        });
        ticket.HasKey(value => value.Id);
        ticket.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value))
            .ValueGeneratedNever();
        ticket.Property(value => value.TenantId).HasColumnName("tenant_id");
        ticket.Property(value => value.Number).HasColumnName("number");
        // El ticket guarda su foto del tipo, pero un tipo sin tickets se puede borrar (fase 4a, plan G5): la FK RESTRICT es la que
        // impide borrar uno usado, también en la carrera contra un alta; SupportUnitOfWork la traduce por nombre.
        ticket.Property(value => value.TypeId)
            .HasColumnName("ticket_type_id")
            .HasConversion(id => id.Value, value => new SupportTicketTypeId(value));
        ticket.HasOne<SupportTicketType>()
            .WithMany()
            .HasForeignKey(value => value.TypeId)
            .HasConstraintName(TicketTypeForeignKey)
            .OnDelete(DeleteBehavior.Restrict);
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
        // El default 'Medium' de la columna vive sólo en la migración, para las filas viejas: declararlo en el modelo haría que EF
        // omitiera Low (el valor CLR por defecto del enum) en el INSERT y la base lo guardara como Medium.
        ticket.Property(value => value.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(24);
        // Responsable y cliente son referencias blandas: viven en otros esquemas.
        ticket.Property(value => value.AssigneeMemberId).HasColumnName("assignee_member_id");
        // Spec 2026-11-22 D-15: since when it has been without an assignee in a status that counts. Nullable, no default: the migration
        // stamps the rows that are in that situation when it runs, and the domain keeps it afterwards.
        ticket.Property(value => value.UnassignedSince).HasColumnName("unassigned_since");
        ticket.Property(value => value.CustomerId).HasColumnName("customer_id");
        // Spec 2026-11-22: quien radicó (el actor del alta; null en el formulario público) y si es una tarea interna.
        ticket.Property(value => value.IsInternal).HasColumnName("is_internal").HasDefaultValue(false);
        ticket.Property(value => value.CreatedByMemberId).HasColumnName("created_by_member_id");
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
        ticket.Property(value => value.ClientRequestId).HasColumnName("client_request_id");
        ticket.Property(value => value.ReceivedAt).HasColumnName("received_at");
        ticket.Property(value => value.ReceivedOn).HasColumnName("received_on");
        ticket.Property(value => value.DueOn).HasColumnName("due_on");
        ticket.Property(value => value.PausedSince).HasColumnName("paused_since");
        ticket.Property(value => value.PausedBusinessDays).HasColumnName("paused_business_days").HasDefaultValue(0);
        ticket.Property(value => value.ResolutionCount).HasColumnName("resolution_count").HasDefaultValue(0);
        // Fase 4a (plan G9): la prórroga única, en días hábiles. Aditivas: los tickets de antes quedan sin prórroga.
        ticket.Property(value => value.ExtensionBusinessDays).HasColumnName("extension_business_days").HasDefaultValue(0);
        ticket.Property(value => value.ExtensionReason)
            .HasColumnName("extension_reason")
            .HasMaxLength(SupportTicket.ExtensionReasonMaxLength);
        ticket.Property(value => value.ExtendedAt).HasColumnName("extended_at");
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
        // Fase 4b (H15): la encuesta que pide ChangeStatus entra en el mismo SaveChanges que el ticket. RESTRICT: un ticket con encuestas
        // no se borra (hoy ningún camino borra tickets).
        ticket.HasMany(value => value.Surveys)
            .WithOne()
            .HasForeignKey(value => value.TicketId)
            .OnDelete(DeleteBehavior.Restrict);
        ticket.Navigation(value => value.Surveys).UsePropertyAccessMode(PropertyAccessMode.Field);
        ticket.HasIndex(value => new { value.TenantId, value.Number }).IsUnique().HasDatabaseName("IX_tickets_tenant_number");
        ticket.HasIndex(value => new { value.TenantId, value.Status, value.ReceivedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_tickets_tenant_status_received");
        ticket.HasIndex(value => new { value.TenantId, value.DueOn }).HasDatabaseName("IX_tickets_tenant_due");
        // Fase 4b (revisión del bloque D): el recorrido del cierre automático, de todos los tenants, por (resolved_at, id) sobre los
        // Resueltos (SupportAutoCloseFeed). El filtro es el mismo predicado de la consulta.
        ticket.HasIndex(value => new { value.ResolvedAt, value.Id })
            .HasFilter("status = 'Resolved'")
            .HasDatabaseName("IX_tickets_resolved_auto_close");
        ticket.HasIndex(value => new { value.TenantId, value.CustomerId }).HasDatabaseName("IX_tickets_tenant_customer");
        // Para SupportUserReferenceProbe, que busca por membresía sola, sin tenant: un usuario se borra en todos sus tenants.
        ticket.HasIndex(value => value.AssigneeMemberId).HasDatabaseName("IX_tickets_assignee");
        // Spec 2026-11-22: la sonda también busca por quien radicó, y «mis tareas creadas» filtra por tenant y creador.
        ticket.HasIndex(value => new { value.TenantId, value.CreatedByMemberId }).HasDatabaseName("IX_tickets_created_by");
        // Spec 2026-11-22 D-15: the alert worker looks only at tickets without an assignee, by tenant.
        ticket.HasIndex(value => value.TenantId).HasFilter("assignee_member_id IS NULL").HasDatabaseName("IX_tickets_unassigned");
        // Los tickets de una conversación: los candidatos de cada entrante y la pestaña de Soporte de la ficha.
        ticket.HasIndex(value => new { value.TenantId, value.ConversationId })
            .HasFilter("conversation_id IS NOT NULL")
            .HasDatabaseName("IX_tickets_tenant_conversation");
        // Spec 2026-11-15 §9.2 (plan L27): the idempotency key of the public filing, unique per tenant. Partial: every ticket without
        // one (other intakes, a filing without key) is outside the index.
        ticket.HasIndex(value => new { value.TenantId, value.ClientRequestId })
            .IsUnique()
            .HasFilter("client_request_id IS NOT NULL")
            .HasDatabaseName(TicketClientRequestIndex);
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

    // Fase 4b (H9). Insert-only: el modelo no tiene mutadores y nada la actualiza; la cascada desde ticket_replies es su único
    // borrado. El `Down` de AddSupportReplyDocuments borraría prueba legal: nunca se corre en producción.
    private static void ConfigureReplyDocument(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<SupportReplyDocument>();
        document.ToTable("ticket_reply_documents", Schema, table =>
        {
            table.HasCheckConstraint("CK_ticket_reply_documents_size", "size_bytes > 0");
            table.HasCheckConstraint(
                "CK_ticket_reply_documents_annexes",
                $"jsonb_typeof(annexes) = 'array' AND jsonb_array_length(annexes) <= {SupportReplyDocument.MaxAnnexes}");
        });
        document.HasKey(value => value.ReplyId);
        document.Property(value => value.ReplyId).HasColumnName("reply_id").ValueGeneratedNever();
        document.Property(value => value.TenantId).HasColumnName("tenant_id");
        document.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        document.Property(value => value.Decision).HasColumnName("decision").HasMaxLength(SupportReplyDocument.DecisionMaxLength);
        document.Property(value => value.NextStep).HasColumnName("next_step").HasMaxLength(SupportFormalNextStep.MaxLength);
        // Membresía del responsable que firma (H3): referencia blanda, la cuenta SupportUserReferenceProbe.
        document.Property(value => value.SignerMemberId).HasColumnName("signer_member_id");
        document.Property(value => value.SignerName).HasColumnName("signer_name").HasMaxLength(SupportReplyDocument.SignerNameMaxLength);
        document.Property(value => value.SignerTitle).HasColumnName("signer_title").HasMaxLength(SupportReplyDocument.SignerTitleMaxLength);
        document.Property(value => value.Annexes)
            .HasColumnName("annexes")
            .HasColumnType("jsonb")
            .HasConversion(AnnexesConverter, AnnexesComparer);
        document.Property(value => value.StorageKey).HasColumnName("storage_key").HasMaxLength(SupportReplyDocument.StorageKeyMaxLength);
        document.Property(value => value.FileName).HasColumnName("file_name").HasMaxLength(SupportReplyDocument.FileNameMaxLength);
        document.Property(value => value.SizeBytes).HasColumnName("size_bytes");
        document.Property(value => value.Sha256Hex).HasColumnName("sha256").HasColumnType("character(64)");
        document.Property(value => value.RespondedOn).HasColumnName("responded_on");
        document.Property(value => value.DocumentClientId).HasColumnName("document_client_id");
        document.Property(value => value.CreatedAt).HasColumnName("created_at");
        document.HasOne<SupportTicketReply>()
            .WithOne()
            .HasForeignKey<SupportReplyDocument>(value => value.ReplyId)
            .OnDelete(DeleteBehavior.Cascade);
        document.HasIndex(value => new { value.TenantId, value.TicketId }).HasDatabaseName("IX_ticket_reply_documents_tenant_ticket");
        // Para SupportUserReferenceProbe, que busca por membresía sola.
        document.HasIndex(value => value.SignerMemberId).HasDatabaseName("IX_ticket_reply_documents_signer");
    }

    // Fase 4b (H15 a H21): una fila por ciclo de resolución. El worker la reclama por IX_ticket_surveys_pending (FOR UPDATE SKIP
    // LOCKED + locked_until); el token del enlace sólo guarda su hash; los ids de mensaje son referencias blandas a messaging.messages.
    private static void ConfigureTicketSurvey(ModelBuilder modelBuilder)
    {
        var survey = modelBuilder.Entity<SupportTicketSurvey>();
        survey.ToTable("ticket_surveys", Schema, table =>
        {
            table.HasCheckConstraint("CK_ticket_surveys_status", InList("status", Enum.GetNames<SupportSurveyStatus>()));
            table.HasCheckConstraint(
                "CK_ticket_surveys_channel", $"channel IS NULL OR {InList("channel", Enum.GetNames<SupportSurveyChannel>())}");
            table.HasCheckConstraint(
                "CK_ticket_surveys_answered_via", $"answered_via IS NULL OR {InList("answered_via", Enum.GetNames<SupportSurveyChannel>())}");
            table.HasCheckConstraint("CK_ticket_surveys_rating", $"rating IS NULL OR {InList("rating", Enum.GetNames<SupportSurveyRating>())}");
            table.HasCheckConstraint("CK_ticket_surveys_comment", $"comment IS NULL OR char_length(comment) <= {SupportTicketSurvey.CommentMaxLength}");
            table.HasCheckConstraint("CK_ticket_surveys_sent_channel", "status <> 'Sent' OR channel IS NOT NULL");
        });
        survey.HasKey(value => value.Id);
        survey.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        survey.Property(value => value.TenantId).HasColumnName("tenant_id");
        survey.Property(value => value.TicketId)
            .HasColumnName("ticket_id")
            .HasConversion(id => id.Value, value => new SupportTicketId(value));
        survey.Property(value => value.ResolutionNumber).HasColumnName("resolution_number");
        survey.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        survey.Property(value => value.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(16);
        survey.Property(value => value.ReasonCode).HasColumnName("reason_code").HasMaxLength(SupportTicketSurvey.ReasonCodeMaxLength);
        survey.Property(value => value.MessageId).HasColumnName("message_id");
        survey.Property(value => value.TokenHash).HasColumnName("token_hash");
        survey.Property(value => value.Attempts).HasColumnName("attempts");
        survey.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at");
        survey.Property(value => value.LockedUntil).HasColumnName("locked_until");
        survey.Property(value => value.SentAt).HasColumnName("sent_at");
        survey.Property(value => value.Rating).HasColumnName("rating").HasConversion<string>().HasMaxLength(16);
        survey.Property(value => value.Comment).HasColumnName("comment").HasMaxLength(SupportTicketSurvey.CommentMaxLength);
        survey.Property(value => value.AnsweredAt).HasColumnName("answered_at");
        survey.Property(value => value.AnsweredVia).HasColumnName("answered_via").HasConversion<string>().HasMaxLength(16);
        survey.Property(value => value.RatingMessageId).HasColumnName("rating_message_id");
        survey.Property(value => value.CommentMessageId).HasColumnName("comment_message_id");
        survey.Property(value => value.CommentUntil).HasColumnName("comment_until");
        survey.Property(value => value.PromptMessageId).HasColumnName("prompt_message_id");
        survey.Property(value => value.PromptSentAt).HasColumnName("prompt_sent_at");
        survey.Property(value => value.PromptFailureCode)
            .HasColumnName("prompt_failure_code")
            .HasMaxLength(SupportTicketSurvey.ReasonCodeMaxLength);
        survey.Property(value => value.CreatedAt).HasColumnName("created_at");
        survey.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        survey.HasIndex(value => new { value.TicketId, value.ResolutionNumber }).IsUnique().HasDatabaseName(TicketSurveyResolutionIndex);
        survey.HasIndex(value => value.TokenHash)
            .IsUnique()
            .HasFilter("token_hash IS NOT NULL")
            .HasDatabaseName("UX_ticket_surveys_token");
        survey.HasIndex(value => value.NextAttemptAt)
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("IX_ticket_surveys_pending");
        survey.HasIndex(value => value.MessageId)
            .HasFilter("message_id IS NOT NULL")
            .HasDatabaseName("IX_ticket_surveys_message");
        survey.HasIndex(value => new { value.TenantId, value.TicketId }).HasDatabaseName("IX_ticket_surveys_tenant_ticket");
        // Revisión del bloque D: los recorridos de SupportSurveyFeed (toques: mandadas sin calificar, por sent_at; comentarios:
        // calificadas por WhatsApp sin comentario, por answered_at) y las búsquedas por id de mensaje del NOT EXISTS de
        // SupportAutoCloseFeed, que mira todas las encuestas del tenant. Cada filtro es el mismo predicado de su consulta.
        survey.HasIndex(value => new { value.SentAt, value.Id })
            .HasFilter("status = 'Sent' AND rating IS NULL")
            .HasDatabaseName("IX_ticket_surveys_awaiting_rating");
        survey.HasIndex(value => new { value.AnsweredAt, value.Id })
            .HasFilter("answered_via = 'WhatsApp' AND rating IS NOT NULL AND comment IS NULL")
            .HasDatabaseName("IX_ticket_surveys_awaiting_comment");
        survey.HasIndex(value => value.RatingMessageId)
            .HasFilter("rating_message_id IS NOT NULL")
            .HasDatabaseName("IX_ticket_surveys_rating_message");
        survey.HasIndex(value => value.CommentMessageId)
            .HasFilter("comment_message_id IS NOT NULL")
            .HasDatabaseName("IX_ticket_surveys_comment_message");
    }

    // Los anexos son una lista corta de textos (H4) que se guarda y se lee entera: jsonb, sin tabla hija.
    private static readonly ValueConverter<IReadOnlyList<string>, string> AnnexesConverter = new(
        annexes => JsonSerializer.Serialize(annexes, (JsonSerializerOptions?)null),
        json => JsonSerializer.Deserialize<string[]>(json, (JsonSerializerOptions?)null) ?? Array.Empty<string>());

    private static readonly ValueComparer<IReadOnlyList<string>> AnnexesComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        annexes => annexes.Aggregate(0, (hash, annex) => HashCode.Combine(hash, annex.GetHashCode(StringComparison.Ordinal))),
        annexes => annexes.ToArray());

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
