using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Messaging.Domain;
using NpgsqlTypes;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>messaging</c> (spec 2026-10-09 §7): <c>conversations</c> (agregado), <c>messages</c>,
/// <c>message_media</c> y <c>webhook_deliveries</c> (filas de lectura; se escriben por SQL). Proyecta
/// <c>audit.entries</c> como Integrations (ExcludeFromMigrations). Lo que EF no modela —extensiones, la
/// configuración de búsqueda y el fillfactor— va en SQL dentro de <c>InitialMessaging</c>.
/// </summary>
public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public const string Schema = "messaging";

    public const string DirectionCheck = "direction IN (1, 2, 3)";
    /// <summary>Spec 2026-10-14 D-I4: 14 es <c>Share</c>.</summary>
    public const string KindCheck = "kind BETWEEN 1 AND 14";

    /// <summary>Spec 2026-10-14 decisión 4.</summary>
    public const string ChannelCheck = "channel IN ('WhatsApp','Instagram')";
    public const string StatusCheck = "status BETWEEN 1 AND 4";

    /// <summary>Spec 2026-10-10 §7.1: un evento es System y Event a la vez, nunca uno sin el otro.</summary>
    public const string SystemIsEventCheck = "(direction = 3) = (kind = 13)";

    /// <summary>§7.1: un evento no fue a WhatsApp (sin wamid), no tiene clientId, está Delivered (D-A5) y lleva su detalle.</summary>
    public const string EventShapeCheck = "direction <> 3 OR (wamid IS NULL AND client_id IS NULL AND status = 2 AND details IS NOT NULL)";

    public const string IdentityCheck = "user_id IS NOT NULL OR wa_id IS NOT NULL";
    public const string AssignmentCheck = "(assigned_member_id IS NULL) = (assigned_at IS NULL)";

    /// <summary>§7.3: calificada con esquema para no depender del search_path de quien inserte.</summary>
    public const string SearchVectorSql = "to_tsvector('messaging.es_unaccent', coalesce(text, '') || ' ' || coalesce(caption, ''))";

    /// <summary>Spec 2026-10-12 §3.4: <c>MessagingUnitOfWork</c> discrimina por este nombre para responder <c>shortcut_taken</c>.</summary>
    public const string QuickReplyShortcutIndex = "IX_quick_replies_tenant_shortcut";

    /// <summary>Spec 2026-10-15 §3.1: único por <c>(tenant_id, lower(name)) WHERE deleted_at IS NULL</c>. Va en la migración con SQL —EF
    /// no modela índices por expresión— y <c>MessagingUnitOfWork</c> lo traduce por este nombre a 409 <c>name_taken</c>.</summary>
    public const string ResolutionReasonNameIndex = "IX_resolution_reasons_tenant_name";

    /// <summary>Spec 2026-10-14 §4.1: los dos únicos del espejo. La unidad de trabajo los traduce por nombre.</summary>
    public const string MessageTemplateMetaIdIndex = "UX_message_templates_meta_id";

    public const string MessageTemplateNameLanguageIndex = "UX_message_templates_name_language";

    /// <summary>Fix de la Task 8 (spec 2026-10-14 §4.5): una sola <c>Quotation</c> por conexión. Parcial sobre <c>kind = 'Quotation'</c>,
    /// que se guarda como texto (<c>CK_message_templates_kind</c>). Dos primeros aprovisionamientos a la vez chocan acá y la unidad de
    /// trabajo lo traduce por nombre a <c>quotation_exists</c>.</summary>
    public const string MessageTemplateQuotationIndex = "UX_message_templates_quotation";

    /// <summary>El PK de <c>template_mirror_syncs</c> (el nombre por defecto de EF, el de la migración <c>AddMessageTemplates</c>): dos
    /// primeros llenados de la misma conexión chocan acá y la unidad de trabajo lo traduce a <c>concurrency.conflict</c>.</summary>
    public const string TemplateMirrorSyncPrimaryKey = "PK_template_mirror_syncs";

    public DbSet<Conversation> Conversations => Set<Conversation>();

    internal DbSet<MessageRecord> Messages => Set<MessageRecord>();

    internal DbSet<MessageMediaRecord> Media => Set<MessageMediaRecord>();

    internal DbSet<WebhookDeliveryRecord> Deliveries => Set<WebhookDeliveryRecord>();

    /// <summary>Spec 2026-10-11 §6.1: el agregado de la campaña.</summary>
    public DbSet<Campaign> Campaigns => Set<Campaign>();

    internal DbSet<CampaignRecipientRecord> CampaignRecipients => Set<CampaignRecipientRecord>();

    /// <summary>Spec 2026-10-12 §3.1: el agregado de la respuesta rápida.</summary>
    public DbSet<QuickReply> QuickReplies => Set<QuickReply>();

    /// <summary>Spec 2026-10-15 §3.1: el catálogo de motivos de resolución.</summary>
    public DbSet<ResolutionReason> ResolutionReasons => Set<ResolutionReason>();

    /// <summary>Spec 2026-10-14 §4.1: el espejo de plantillas.</summary>
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();

    internal DbSet<TemplateMirrorSyncRecord> TemplateMirrorSyncs => Set<TemplateMirrorSyncRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConversation(modelBuilder);
        ConfigureMessage(modelBuilder);
        ConfigureMedia(modelBuilder);
        ConfigureDelivery(modelBuilder);
        ConfigureSendBudget(modelBuilder);
        ConfigureCampaign(modelBuilder);
        ConfigureCampaignRecipient(modelBuilder);
        ConfigureQuickReply(modelBuilder);
        ConfigureResolutionReason(modelBuilder);
        ConfigureMessageTemplate(modelBuilder);
        ConfigureTemplateMirrorSync(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureConversation(ModelBuilder modelBuilder)
    {
        var conversation = modelBuilder.Entity<Conversation>();
        conversation.ToTable("conversations", Schema, table =>
        {
            table.HasCheckConstraint("CK_conversations_status", "status IN ('Open','Resolved')");
            table.HasCheckConstraint("CK_conversations_unread", "unread_count >= 0");
            table.HasCheckConstraint("CK_conversations_identity", IdentityCheck);
            table.HasCheckConstraint("CK_conversations_assignment", AssignmentCheck);
            table.HasCheckConstraint("CK_conversations_channel", ChannelCheck);
        });
        conversation.HasKey(value => value.Id);
        conversation.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        conversation.Property(value => value.TenantId).HasColumnName("tenant_id");
        conversation.Property(value => value.ConnectionId).HasColumnName("connection_id");
        // Spec 2026-10-14 decisión 4: por nombre, con default para las filas que ya existían (todas de WhatsApp). El centinela hace
        // que EF escriba siempre la columna, también cuando vale WhatsApp (el 0 del enum).
        conversation.Property(value => value.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(16)
            .HasDefaultValue(ConversationChannel.WhatsApp).HasSentinel((ConversationChannel)(-1));
        conversation.Property(value => value.UserId).HasColumnName("user_id").HasMaxLength(Conversation.UserIdMaxLength);
        conversation.Property(value => value.WaId).HasColumnName("wa_id").HasMaxLength(Conversation.WaIdMaxLength);
        conversation.Property(value => value.Username).HasColumnName("username").HasMaxLength(Conversation.UsernameMaxLength);
        conversation.Property(value => value.ParentUserId).HasColumnName("parent_user_id").HasMaxLength(Conversation.UserIdMaxLength);
        conversation.Property(value => value.CustomerId).HasColumnName("customer_id");
        conversation.Property(value => value.AssignedMemberId).HasColumnName("assigned_member_id");
        conversation.Property(value => value.AssignedAt).HasColumnName("assigned_at");
        conversation.Property(value => value.ProfileName).HasColumnName("profile_name").HasMaxLength(Conversation.ProfileNameMaxLength);
        conversation.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        conversation.Property(value => value.UnreadCount).HasColumnName("unread_count").HasDefaultValue(0);
        conversation.Property(value => value.LastInboundAt).HasColumnName("last_inbound_at");
        conversation.Property(value => value.LastInboundWamid).HasColumnName("last_inbound_wamid");
        conversation.Property(value => value.LastActivityAt).HasColumnName("last_activity_at");
        conversation.Property(value => value.LastMessageId).HasColumnName("last_message_id");
        conversation.Property(value => value.LastMessageDirection).HasColumnName("last_message_direction")
            .HasConversion(value => value == null ? (short?)null : MessageColumnCodes.ToCode(value.Value), code => code == null ? null : MessageColumnCodes.ToDirection(code.Value));
        conversation.Property(value => value.LastMessageKind).HasColumnName("last_message_kind")
            .HasConversion(value => value == null ? (short?)null : MessageColumnCodes.ToCode(value.Value), code => code == null ? null : MessageColumnCodes.ToKind(code.Value));
        conversation.Property(value => value.LastMessagePreview).HasColumnName("last_message_preview").HasMaxLength(Conversation.PreviewMaxLength);
        conversation.Property(value => value.LastMessageStatus).HasColumnName("last_message_status")
            .HasConversion(value => value == null ? (short?)null : MessageColumnCodes.ToCode(value.Value), code => code == null ? null : MessageColumnCodes.ToStatus(code.Value));
        conversation.Property(value => value.LastMessageAt).HasColumnName("last_message_at");
        conversation.Property(value => value.CreatedAt).HasColumnName("created_at");
        conversation.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        conversation.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        // Spec 2026-10-15 §3.1: el motivo vigente. RESTRICT porque una fila de motivo nunca se borra (D-R5: archivar es deleted_at).
        conversation.Property(value => value.ResolutionReasonId).HasColumnName("resolution_reason_id");
        conversation.HasOne<ResolutionReason>().WithMany().HasForeignKey(value => value.ResolutionReasonId)
            .HasConstraintName("FK_conversations_resolution_reasons").OnDelete(DeleteBehavior.Restrict);
        conversation.Ignore(value => value.CustomerWindowExpiresAt);

        // Todos con el overload con nombre: sin nombre en HasIndex, EF funde los que tienen las mismas columnas
        // y el segundo pisa el filtro del primero.
        // Spec 2026-10-10 §7.1: la clave nueva (blanco del ON CONFLICT de la ingesta) y la vieja, sólo entre filas sin BSUID.
        conversation.HasIndex(value => new { value.ConnectionId, value.UserId }, "IX_conversations_connection_user")
            .IsUnique().HasFilter("user_id IS NOT NULL");
        conversation.HasIndex(value => new { value.ConnectionId, value.WaId }, "IX_conversations_connection_wa_legacy")
            .IsUnique().HasFilter("user_id IS NULL");
        conversation.HasIndex(value => new { value.TenantId, value.Status, value.LastActivityAt, value.Id }, "IX_conversations_tenant_status_activity")
            .IsDescending(false, false, true, true);
        // Pestaña «Mías» y counts.mine; pestaña «Sin asignar» y counts.unassigned.
        conversation.HasIndex(value => new { value.TenantId, value.AssignedMemberId, value.Status, value.LastActivityAt, value.Id }, "IX_conversations_tenant_assignee_status_activity")
            .IsDescending(false, false, false, true, true).HasFilter("assigned_member_id IS NOT NULL");
        conversation.HasIndex(value => new { value.TenantId, value.Status, value.LastActivityAt, value.Id }, "IX_conversations_tenant_unassigned_status_activity")
            .IsDescending(false, false, true, true).HasFilter("assigned_member_id IS NULL");
        // §8.2: la herencia del asignado — la conversación más reciente del cliente.
        conversation.HasIndex(value => new { value.TenantId, value.CustomerId, value.LastActivityAt }, "IX_conversations_tenant_customer_activity")
            .IsDescending(false, false, true).HasFilter("customer_id IS NOT NULL");
        conversation.HasIndex(value => value.TenantId, "IX_conversations_tenant_open").HasFilter("status = 'Open'");
        conversation.HasIndex(value => value.TenantId, "IX_conversations_tenant_unread")
            .HasFilter("unread_count > 0").IncludeProperties(value => value.UnreadCount);
        conversation.HasIndex(value => value.ProfileName, "IX_conversations_profile_name_trgm")
            .HasMethod("gin").HasOperators("gin_trgm_ops");
        conversation.HasIndex(value => value.WaId, "IX_conversations_wa_id_trgm")
            .HasMethod("gin").HasOperators("gin_trgm_ops");
        conversation.HasIndex(value => value.Username, "IX_conversations_username_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        // Spec 2026-10-14 §5.2: el filtro por canal de la lista, con la misma forma que el índice de la lista.
        conversation.HasIndex(value => new { value.TenantId, value.Channel, value.Status, value.LastActivityAt, value.Id }, "IX_conversations_tenant_channel_status_activity")
            .IsDescending(false, false, false, true, true);
    }

    private static void ConfigureMessage(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<MessageRecord>();
        message.ToTable("messages", Schema, table =>
        {
            table.HasCheckConstraint("CK_messages_direction", DirectionCheck);
            table.HasCheckConstraint("CK_messages_kind", KindCheck);
            table.HasCheckConstraint("CK_messages_status", StatusCheck);
            table.HasCheckConstraint("CK_messages_inbound_not_failed", "direction = 2 OR status <> 4");
            table.HasCheckConstraint("CK_messages_system_is_event", SystemIsEventCheck);
            table.HasCheckConstraint("CK_messages_event_shape", EventShapeCheck);
        });
        message.HasKey(value => value.Id);
        message.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        message.Property(value => value.ConversationId).HasColumnName("conversation_id");
        message.Property(value => value.TenantId).HasColumnName("tenant_id");
        message.Property(value => value.ConnectionId).HasColumnName("connection_id");
        message.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        message.Property(value => value.Direction).HasColumnName("direction").HasColumnType("smallint")
            .HasConversion(value => MessageColumnCodes.ToCode(value), code => MessageColumnCodes.ToDirection(code));
        message.Property(value => value.Kind).HasColumnName("kind").HasColumnType("smallint")
            .HasConversion(value => MessageColumnCodes.ToCode(value), code => MessageColumnCodes.ToKind(code));
        message.Property(value => value.Status).HasColumnName("status").HasColumnType("smallint")
            .HasConversion(value => MessageColumnCodes.ToCode(value), code => MessageColumnCodes.ToStatus(code));
        message.Property(value => value.Text).HasColumnName("text");
        message.Property(value => value.Caption).HasColumnName("caption").HasMaxLength(1024);
        message.Property(value => value.Details).HasColumnName("details").HasColumnType("jsonb");
        message.Property(value => value.Wamid).HasColumnName("wamid");
        message.Property(value => value.ClientId).HasColumnName("client_id");
        message.Property(value => value.SentByMemberId).HasColumnName("sent_by_member_id");
        message.Property(value => value.ReplyToMessageId).HasColumnName("reply_to_message_id");
        message.Property(value => value.ReplyToWamid).HasColumnName("reply_to_wamid");
        message.Property(value => value.FailureCode).HasColumnName("failure_code");
        message.Property(value => value.FailureTitle).HasColumnName("failure_title");
        message.Property(value => value.CreatedAt).HasColumnName("created_at");
        // §7.3: generada y almacenada; EF la lee pero nunca la escribe.
        message.Property<NpgsqlTsVector>("SearchVector").HasColumnName("search_vector")
            .HasComputedColumnSql(SearchVectorSql, stored: true);
        message.HasOne<Conversation>().WithMany().HasForeignKey(value => value.ConversationId)
            .HasConstraintName("FK_messages_conversation").OnDelete(DeleteBehavior.Cascade);
        message.HasOne(value => value.Media).WithOne().HasForeignKey<MessageMediaRecord>(value => value.MessageId)
            .HasConstraintName("FK_message_media_message").OnDelete(DeleteBehavior.Cascade);

        message.HasIndex(value => new { value.ConversationId, value.OccurredAt, value.Id })
            .IsDescending(false, true, true).HasDatabaseName("IX_messages_thread");
        message.HasIndex(value => new { value.ConnectionId, value.Wamid }).IsUnique()
            .HasDatabaseName("IX_messages_connection_wamid").HasFilter("wamid IS NOT NULL");
        message.HasIndex(value => new { value.ConversationId, value.ClientId }).IsUnique()
            .HasDatabaseName("IX_messages_conversation_client").HasFilter("client_id IS NOT NULL");
        message.HasIndex(nameof(MessageRecord.TenantId), "SearchVector").HasDatabaseName("IX_messages_tenant_search").HasMethod("gin");
    }

    private static void ConfigureMedia(ModelBuilder modelBuilder)
    {
        var media = modelBuilder.Entity<MessageMediaRecord>();
        media.ToTable("message_media", Schema);
        media.HasKey(value => value.MessageId);
        media.Property(value => value.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        media.Property(value => value.MimeType).HasColumnName("mime_type").HasMaxLength(128);
        media.Property(value => value.FileName).HasColumnName("file_name").HasMaxLength(256);
        media.Property(value => value.MetaMediaId).HasColumnName("meta_media_id").HasMaxLength(64);
        media.Property(value => value.SizeBytes).HasColumnName("size_bytes");
        media.Property(value => value.Sha256).HasColumnName("sha256").HasMaxLength(64);
        media.Property(value => value.StorageKey).HasColumnName("storage_key").HasMaxLength(256);
        media.Property(value => value.StoredAt).HasColumnName("stored_at");
        media.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        media.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at");
        media.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(256);
        // Spec 2026-10-14 §6.2: la URL pública (firmada, con vencimiento) del adjunto de Instagram; null en WhatsApp.
        media.Property(value => value.SourceUrl).HasColumnName("source_url");
        media.HasIndex(value => value.NextAttemptAt).HasDatabaseName("IX_message_media_pending").HasFilter("stored_at IS NULL");
    }

    private static readonly ValueConverter<IReadOnlyList<Guid>, Guid[]> GuidListConverter = new(list => list.ToArray(), array => array);

    private static readonly ValueComparer<IReadOnlyList<Guid>> GuidListComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        list => list.Aggregate(0, (hash, id) => HashCode.Combine(hash, id)),
        list => list.ToArray());

    private static void ConfigureCampaign(ModelBuilder modelBuilder)
    {
        var campaign = modelBuilder.Entity<Campaign>();
        campaign.ToTable("campaigns", Schema, table =>
        {
            table.HasCheckConstraint("CK_campaigns_status", "status IN ('Draft','Scheduled','Sending','Completed','Cancelled','Failed')");
            table.HasCheckConstraint("CK_campaigns_customers_cap", "cardinality(audience_customer_ids) <= 500");
            table.HasCheckConstraint("CK_campaigns_audience", "cardinality(audience_tag_ids) + cardinality(audience_customer_ids) > 0");
        });
        campaign.HasKey(value => value.Id);
        campaign.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        campaign.Property(value => value.TenantId).HasColumnName("tenant_id");
        campaign.Property(value => value.ConnectionId).HasColumnName("connection_id");
        campaign.Property(value => value.Name).HasColumnName("name").HasMaxLength(Campaign.NameMaxLength);
        campaign.Property(value => value.TemplateName).HasColumnName("template_name").HasMaxLength(512);
        campaign.Property(value => value.TemplateLanguage).HasColumnName("template_language").HasMaxLength(16);
        campaign.Property(value => value.TemplateCategory).HasColumnName("template_category").HasMaxLength(32);
        campaign.Property(value => value.Parameters).HasColumnName("parameters").HasColumnType("jsonb")
            .HasConversion(CampaignParametersJson.Converter, CampaignParametersJson.Comparer);
        campaign.Property(value => value.AudienceTagIds).HasColumnName("audience_tag_ids").HasColumnType("uuid[]")
            .HasConversion(GuidListConverter, GuidListComparer);
        campaign.Property(value => value.AudienceCustomerIds).HasColumnName("audience_customer_ids").HasColumnType("uuid[]")
            .HasConversion(GuidListConverter, GuidListComparer);
        campaign.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        campaign.Property(value => value.FailureReason).HasColumnName("failure_reason").HasMaxLength(512);
        campaign.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        campaign.Property(value => value.ScheduledAt).HasColumnName("scheduled_at");
        campaign.Property(value => value.StartedAt).HasColumnName("started_at");
        campaign.Property(value => value.CompletedAt).HasColumnName("completed_at");
        campaign.Property(value => value.AudienceFrozenAt).HasColumnName("audience_frozen_at");
        campaign.Property(value => value.RetryCount).HasColumnName("retry_count").HasDefaultValue(0);
        campaign.Property(value => value.CreatedByMemberId).HasColumnName("created_by_member_id");
        campaign.Property(value => value.CreatedAt).HasColumnName("created_at");
        campaign.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        campaign.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        campaign.HasIndex(value => new { value.TenantId, value.CreatedAt }, "IX_campaigns_tenant_created").IsDescending(false, true);
        // El worker busca las programadas que ya vencieron y las que están enviándose; ninguna de las dos es la mayoría de la tabla.
        campaign.HasIndex(value => value.ScheduledAt, "IX_campaigns_scheduled").HasFilter("status = 'Scheduled'");
        campaign.HasIndex(value => value.Status, "IX_campaigns_sending").HasFilter("status = 'Sending'");
    }

    private static void ConfigureCampaignRecipient(ModelBuilder modelBuilder)
    {
        var recipient = modelBuilder.Entity<CampaignRecipientRecord>();
        recipient.ToTable("campaign_recipients", Schema, table =>
        {
            table.HasCheckConstraint("CK_campaign_recipients_status", "status IN ('Pending','Sending','Sent','Failed','Skipped')");
        });
        recipient.HasKey(value => value.Id);
        recipient.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        recipient.Property(value => value.CampaignId).HasColumnName("campaign_id");
        recipient.Property(value => value.TenantId).HasColumnName("tenant_id");
        recipient.Property(value => value.CustomerId).HasColumnName("customer_id");
        recipient.Property(value => value.CustomerName).HasColumnName("customer_name").HasMaxLength(256);
        recipient.Property(value => value.WaId).HasColumnName("wa_id").HasMaxLength(Conversation.WaIdMaxLength);
        recipient.Property(value => value.UserId).HasColumnName("user_id").HasMaxLength(Conversation.UserIdMaxLength);
        recipient.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        recipient.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        recipient.Property(value => value.FailureReason).HasColumnName("failure_reason").HasMaxLength(512);
        recipient.Property(value => value.ConversationId).HasColumnName("conversation_id");
        recipient.Property(value => value.MessageId).HasColumnName("message_id");
        recipient.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        recipient.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at");
        recipient.Property(value => value.LockedUntil).HasColumnName("locked_until");
        recipient.Property(value => value.SentAt).HasColumnName("sent_at");
        recipient.Property(value => value.RetryCount).HasColumnName("retry_count").HasDefaultValue(0);
        recipient.HasOne<Campaign>().WithMany().HasForeignKey(value => value.CampaignId)
            .HasConstraintName("FK_campaign_recipients_campaign").OnDelete(DeleteBehavior.Cascade);
        // El blanco del ON CONFLICT DO NOTHING con el que el worker congela la audiencia: repetirlo es inocuo.
        recipient.HasIndex(value => new { value.CampaignId, value.CustomerId }, "IX_campaign_recipients_campaign_customer").IsUnique();
        recipient.HasIndex(value => value.NextAttemptAt, "IX_campaign_recipients_due").HasFilter("status IN ('Pending','Sending')");
        // Los contadores y el filtro por estado del detalle.
        recipient.HasIndex(value => new { value.CampaignId, value.Status }, "IX_campaign_recipients_campaign_status");
        // El fallo que Meta avisa después de aceptar (StatusIngestion) busca al destinatario por su mensaje: sin índice, cada
        // acuse failed recorrería la tabla entera. Parcial porque los Pending y los Skipped no tienen mensaje.
        recipient.HasIndex(value => value.MessageId, "IX_campaign_recipients_message").HasFilter("message_id IS NOT NULL");
    }

    /// <summary>Spec 2026-10-12 §3.1: el atajo es único por tenant. El índice va con nombre porque la unidad de trabajo lo reconoce
    /// por nombre (§3.4).</summary>
    private static void ConfigureQuickReply(ModelBuilder modelBuilder)
    {
        var quickReply = modelBuilder.Entity<QuickReply>();
        quickReply.ToTable("quick_replies", Schema);
        quickReply.HasKey(value => value.Id);
        quickReply.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        quickReply.Property(value => value.TenantId).HasColumnName("tenant_id");
        quickReply.Property(value => value.Shortcut).HasColumnName("shortcut").HasMaxLength(QuickReply.ShortcutMaxLength);
        quickReply.Property(value => value.Body).HasColumnName("body").HasMaxLength(QuickReply.BodyMaxLength);
        quickReply.Property(value => value.CreatedAt).HasColumnName("created_at");
        quickReply.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        quickReply.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        quickReply.HasIndex(value => new { value.TenantId, value.Shortcut }, QuickReplyShortcutIndex).IsUnique();
    }

    /// <summary>Spec 2026-10-15 §3.1. El único por nombre (<see cref="ResolutionReasonNameIndex"/>) va por SQL en la migración.</summary>
    private static void ConfigureResolutionReason(ModelBuilder modelBuilder)
    {
        var reason = modelBuilder.Entity<ResolutionReason>();
        reason.ToTable("resolution_reasons", Schema);
        reason.HasKey(value => value.Id);
        reason.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        reason.Property(value => value.TenantId).HasColumnName("tenant_id");
        reason.Property(value => value.Name).HasColumnName("name").HasMaxLength(ResolutionReason.NameMaxLength);
        reason.Property(value => value.CreatedAt).HasColumnName("created_at");
        reason.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        reason.Property(value => value.DeletedAt).HasColumnName("deleted_at");
        reason.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        reason.Ignore(value => value.IsArchived);
    }

    /// <summary>Spec 2026-10-14 §4.1. Sin FK a la conexión: es de Integrations, otro schema (como el cubo de envío).</summary>
    private static void ConfigureMessageTemplate(ModelBuilder modelBuilder)
    {
        var template = modelBuilder.Entity<MessageTemplate>();
        template.ToTable("message_templates", Schema, table =>
            table.HasCheckConstraint("CK_message_templates_kind", "kind IN ('Free','Quotation','External')"));
        template.HasKey(value => value.Id);
        template.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        template.Property(value => value.TenantId).HasColumnName("tenant_id");
        template.Property(value => value.ConnectionId).HasColumnName("connection_id");
        template.Property(value => value.MetaTemplateId).HasColumnName("meta_template_id").HasMaxLength(MessageTemplate.MetaTemplateIdMaxLength);
        template.Property(value => value.Name).HasColumnName("name").HasMaxLength(MessageTemplateName.MaxLength);
        template.Property(value => value.Language).HasColumnName("language").HasMaxLength(MessageTemplate.LanguageMaxLength);
        template.Property(value => value.Category).HasColumnName("category").HasMaxLength(MessageTemplate.CategoryMaxLength);
        template.Property(value => value.ParameterFormat).HasColumnName("parameter_format").HasMaxLength(MessageTemplate.ParameterFormatMaxLength);
        template.Property(value => value.Status).HasColumnName("status").HasMaxLength(MessageTemplateStatuses.MaxLength);
        template.Property(value => value.StatusReason).HasColumnName("status_reason").HasMaxLength(MessageTemplate.StatusReasonMaxLength);
        template.Property(value => value.ComponentsJson).HasColumnName("components").HasColumnType("jsonb");
        template.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        template.Property(value => value.CreatedByMemberId).HasColumnName("created_by_member_id");
        template.Property(value => value.CreatedAt).HasColumnName("created_at");
        template.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        template.Property(value => value.SyncedAt).HasColumnName("synced_at");
        template.HasIndex(value => new { value.TenantId, value.ConnectionId, value.MetaTemplateId }, MessageTemplateMetaIdIndex).IsUnique();
        template.HasIndex(value => new { value.TenantId, value.ConnectionId, value.Name, value.Language }, MessageTemplateNameLanguageIndex).IsUnique();
        template.HasIndex(value => new { value.TenantId, value.ConnectionId }, MessageTemplateQuotationIndex).IsUnique().HasFilter("kind = 'Quotation'");
        template.HasIndex(value => new { value.TenantId, value.ConnectionId, value.Status }, "IX_message_templates_status");
    }

    private static void ConfigureTemplateMirrorSync(ModelBuilder modelBuilder)
    {
        var sync = modelBuilder.Entity<TemplateMirrorSyncRecord>();
        sync.ToTable("template_mirror_syncs", Schema);
        sync.HasKey(value => new { value.TenantId, value.ConnectionId });
        sync.Property(value => value.TenantId).HasColumnName("tenant_id");
        sync.Property(value => value.ConnectionId).HasColumnName("connection_id");
        sync.Property(value => value.SyncedAt).HasColumnName("synced_at");
    }

    /// <summary>Decisión del owner 2026-10-12 (P3 y P5): el cubo de envío por conexión. Sin FK: la conexión es de Integrations, otro schema.</summary>
    private static void ConfigureSendBudget(ModelBuilder modelBuilder)
    {
        var budget = modelBuilder.Entity<ConnectionSendBudgetRecord>();
        budget.ToTable("connection_send_budgets", Schema, table =>
        {
            table.HasCheckConstraint("CK_connection_send_budgets_rate", "rate >= 1");
            table.HasCheckConstraint("CK_connection_send_budgets_tokens", "tokens >= 0");
        });
        budget.HasKey(value => value.ConnectionId);
        budget.Property(value => value.ConnectionId).HasColumnName("connection_id").ValueGeneratedNever();
        budget.Property(value => value.TenantId).HasColumnName("tenant_id");
        budget.Property(value => value.Tokens).HasColumnName("tokens");
        budget.Property(value => value.Rate).HasColumnName("rate");
        budget.Property(value => value.RefilledAt).HasColumnName("refilled_at");
        budget.Property(value => value.ThrottledUntil).HasColumnName("throttled_until");
        budget.Property(value => value.UpdatedAt).HasColumnName("updated_at");
    }

    private static void ConfigureDelivery(ModelBuilder modelBuilder)
    {
        var delivery = modelBuilder.Entity<WebhookDeliveryRecord>();
        delivery.ToTable("webhook_deliveries", Schema);
        delivery.HasKey(value => value.Id);
        delivery.Property(value => value.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        delivery.Property(value => value.BodySha256).HasColumnName("body_sha256");
        delivery.Property(value => value.Payload).HasColumnName("payload").HasColumnType("jsonb");
        delivery.Property(value => value.ReceivedAt).HasColumnName("received_at");
        delivery.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        delivery.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        delivery.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        delivery.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(512);
        // Spec 2026-10-14 §6.2: con qué parser se procesa; las filas anteriores eran todas de WhatsApp.
        delivery.Property(value => value.Provider).HasColumnName("provider").HasMaxLength(16).HasDefaultValue("whatsapp");
        delivery.HasIndex(value => value.BodySha256).IsUnique().HasDatabaseName("IX_webhook_deliveries_body_sha256");
        delivery.HasIndex(value => value.Id).HasDatabaseName("IX_webhook_deliveries_pending").HasFilter("processed_at IS NULL");
    }
}
