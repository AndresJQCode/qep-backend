using Microsoft.EntityFrameworkCore;
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
    public const string KindCheck = "kind BETWEEN 1 AND 13";
    public const string StatusCheck = "status BETWEEN 1 AND 4";

    /// <summary>Spec 2026-10-10 §7.1: un evento es System y Event a la vez, nunca uno sin el otro.</summary>
    public const string SystemIsEventCheck = "(direction = 3) = (kind = 13)";

    /// <summary>§7.1: un evento no fue a WhatsApp (sin wamid), no tiene clientId, está Delivered (D-A5) y lleva su detalle.</summary>
    public const string EventShapeCheck = "direction <> 3 OR (wamid IS NULL AND client_id IS NULL AND status = 2 AND details IS NOT NULL)";

    public const string IdentityCheck = "user_id IS NOT NULL OR wa_id IS NOT NULL";
    public const string AssignmentCheck = "(assigned_member_id IS NULL) = (assigned_at IS NULL)";

    /// <summary>§7.3: calificada con esquema para no depender del search_path de quien inserte.</summary>
    public const string SearchVectorSql = "to_tsvector('messaging.es_unaccent', coalesce(text, '') || ' ' || coalesce(caption, ''))";

    public DbSet<Conversation> Conversations => Set<Conversation>();

    internal DbSet<MessageRecord> Messages => Set<MessageRecord>();

    internal DbSet<MessageMediaRecord> Media => Set<MessageMediaRecord>();

    internal DbSet<WebhookDeliveryRecord> Deliveries => Set<WebhookDeliveryRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConversation(modelBuilder);
        ConfigureMessage(modelBuilder);
        ConfigureMedia(modelBuilder);
        ConfigureDelivery(modelBuilder);
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
        });
        conversation.HasKey(value => value.Id);
        conversation.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        conversation.Property(value => value.TenantId).HasColumnName("tenant_id");
        conversation.Property(value => value.ConnectionId).HasColumnName("connection_id");
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
        media.HasIndex(value => value.NextAttemptAt).HasDatabaseName("IX_message_media_pending").HasFilter("stored_at IS NULL");
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
        delivery.HasIndex(value => value.BodySha256).IsUnique().HasDatabaseName("IX_webhook_deliveries_body_sha256");
        delivery.HasIndex(value => value.Id).HasDatabaseName("IX_webhook_deliveries_pending").HasFilter("processed_at IS NULL");
    }
}
