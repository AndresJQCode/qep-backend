using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>pipeline</c> (spec 2026-10-13 §4): embudo y etapas, negocios con su historial append-only, la foto de las
/// cotizaciones, los catálogos de motivos, las decisiones de triage y el inbox del worker. Proyecta <c>audit.entries</c> y
/// <c>platform.outbox_messages</c> sin ser su dueño (ExcludeFromMigrations). Sin FKs a otros esquemas: clientes, conversaciones,
/// membresías y cotizaciones son referencias blandas.
/// </summary>
public sealed class PipelineDbContext(DbContextOptions<PipelineDbContext> options) : DbContext(options)
{
    public const string Schema = "pipeline";

    /// <summary>D4: el blanco del <c>ON CONFLICT</c> de <c>PipelineProvisioner</c>.</summary>
    public const string PipelineTenantIndex = "IX_pipelines_tenant";

    /// <summary>D38: <c>PipelineUnitOfWork</c> discrimina por este nombre para responder <c>pipeline.triage.already_decided</c>.</summary>
    public const string TriageDecisionInboundIndex = "IX_triage_decisions_conversation_inbound";

    /// <summary>D12: <c>PipelineUnitOfWork</c> discrimina por este nombre para responder <c>pipeline.stage.has_deals</c>.</summary>
    public const string DealStageForeignKey = "FK_deals_stages_stage_id";

    /// <summary>P4 del plan: <c>PipelineUnitOfWork</c> discrimina por este nombre para responder <c>concurrency.conflict</c>.</summary>
    public const string DealHistoryOrdinalIndex = "IX_deal_stage_history_deal_ordinal";

    public DbSet<SalesPipeline> Pipelines => Set<SalesPipeline>();

    public DbSet<PipelineStage> Stages => Set<PipelineStage>();

    public DbSet<Deal> Deals => Set<Deal>();

    public DbSet<DealStageHistoryEntry> History => Set<DealStageHistoryEntry>();

    public DbSet<DealQuotation> DealQuotations => Set<DealQuotation>();

    public DbSet<DealOrder> DealOrders => Set<DealOrder>();

    public DbSet<PipelineReason> Reasons => Set<PipelineReason>();

    public DbSet<TriageDecision> TriageDecisions => Set<TriageDecision>();

    internal DbSet<PipelineInboxMessage> Inbox => Set<PipelineInboxMessage>();

    internal DbSet<PipelineOutboxRecord> Outbox => Set<PipelineOutboxRecord>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigurePipeline(modelBuilder);
        ConfigureStage(modelBuilder);
        ConfigureDeal(modelBuilder);
        ConfigureHistory(modelBuilder);
        ConfigureDealQuotation(modelBuilder);
        ConfigureDealOrder(modelBuilder);
        ConfigureReason(modelBuilder);
        ConfigureTriageDecision(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigurePipeline(ModelBuilder modelBuilder)
    {
        var pipeline = modelBuilder.Entity<SalesPipeline>();
        pipeline.ToTable("pipelines", Schema);
        pipeline.HasKey(value => value.Id);
        pipeline.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        pipeline.Property(value => value.TenantId).HasColumnName("tenant_id");
        pipeline.Property(value => value.Name).HasColumnName("name").HasMaxLength(SalesPipeline.NameMaxLength);
        pipeline.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        pipeline.Property(value => value.CreatedAt).HasColumnName("created_at");
        pipeline.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // D10: uno por tenant en este slice. Varios embudos después = quitar este índice.
        pipeline.HasIndex(value => value.TenantId, PipelineTenantIndex).IsUnique();
        pipeline.HasMany(value => value.Stages).WithOne().HasForeignKey(stage => stage.PipelineId).OnDelete(DeleteBehavior.Cascade);
        pipeline.Navigation(value => value.Stages).UsePropertyAccessMode(PropertyAccessMode.Field);
        pipeline.Ignore(value => value.OrderedStages);
        pipeline.Ignore(value => value.FirstOpenStage);
        pipeline.Ignore(value => value.WonStage);
        pipeline.Ignore(value => value.LostStage);
        pipeline.Ignore(value => value.QuotationStage);
    }

    private static void ConfigureStage(ModelBuilder modelBuilder)
    {
        var stage = modelBuilder.Entity<PipelineStage>();
        stage.ToTable("stages", Schema, table =>
        {
            table.HasCheckConstraint("CK_stages_kind", "kind IN ('Open','Won','Lost')");
            table.HasCheckConstraint("CK_stages_probability", "probability BETWEEN 0 AND 100");
        });
        stage.HasKey(value => value.Id);
        stage.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        stage.Property(value => value.PipelineId).HasColumnName("pipeline_id");
        stage.Property(value => value.Name).HasColumnName("name").HasMaxLength(SalesPipeline.StageNameMaxLength);
        stage.Property(value => value.Position).HasColumnName("position");
        stage.Property(value => value.Probability).HasColumnName("probability");
        stage.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(8);
        stage.Property(value => value.StallDays).HasColumnName("stall_days");
        stage.Property(value => value.IsQuotationStage).HasColumnName("is_quotation_stage");
        stage.HasIndex(value => new { value.PipelineId, value.Position }, "IX_stages_pipeline_position");
    }

    private static void ConfigureDeal(ModelBuilder modelBuilder)
    {
        var deal = modelBuilder.Entity<Deal>();
        deal.ToTable("deals", Schema, table =>
        {
            table.HasCheckConstraint("CK_deals_source", "source IN ('Manual','Conversation','Quotation','Order')");
            table.HasCheckConstraint("CK_deals_amount", "amount IS NULL OR amount >= 0");
        });
        deal.HasKey(value => value.Id);
        deal.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        deal.Property(value => value.TenantId).HasColumnName("tenant_id");
        deal.Property(value => value.PipelineId).HasColumnName("pipeline_id");
        deal.Property(value => value.CustomerId).HasColumnName("customer_id");
        deal.Property(value => value.OwnerMemberId).HasColumnName("owner_member_id");
        deal.Property(value => value.Title).HasColumnName("title").HasMaxLength(Deal.TitleMaxLength);
        deal.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        deal.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        deal.Property(value => value.ExpectedCloseDate).HasColumnName("expected_close_date");
        deal.Property(value => value.StageId).HasColumnName("stage_id");
        deal.Property(value => value.StageEnteredAt).HasColumnName("stage_entered_at");
        deal.Property(value => value.PrimaryQuotationId).HasColumnName("primary_quotation_id");
        deal.Property(value => value.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(16);
        deal.Property(value => value.OriginConversationId).HasColumnName("origin_conversation_id");
        deal.Property(value => value.LossReasonId).HasColumnName("loss_reason_id");
        deal.Property(value => value.LossNote).HasColumnName("loss_note").HasMaxLength(Deal.LossNoteMaxLength);
        deal.Property(value => value.ClosedAt).HasColumnName("closed_at");
        deal.Property(value => value.LastChangedAt).HasColumnName("last_changed_at");
        deal.Property(value => value.CreatedAt).HasColumnName("created_at");
        deal.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        deal.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        // P4 del plan: de acá sale el ordinal de la próxima fila de historial.
        deal.Property(value => value.HistoryCount).HasColumnName("history_count");
        deal.Ignore(value => value.IsClosed);
        // D12: la base garantiza que no se borre una etapa con negocios, aunque un negocio llegue entre la lectura de
        // StagesWithDealsAsync y el commit. PipelineUnitOfWork traduce la violación a pipeline.stage.has_deals.
        deal.HasOne<PipelineStage>().WithMany().HasForeignKey(value => value.StageId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(DealStageForeignKey);
        // §4.4 y §5.1: los abiertos de un cliente (enlace automático, triage, ficha).
        deal.HasIndex(value => new { value.TenantId, value.CustomerId }, "IX_deals_tenant_customer_open").HasFilter("closed_at IS NULL");
        // D33: el tablero por etapa, en orden de llegada a la etapa.
        deal.HasIndex(value => new { value.TenantId, value.StageId, value.StageEnteredAt }, "IX_deals_tenant_stage");
        // D33 y D36: la ventana de 30 días de Won/Lost y el último cerrado del cliente.
        deal.HasIndex(value => new { value.TenantId, value.CustomerId, value.ClosedAt }, "IX_deals_tenant_customer_closed").HasFilter("closed_at IS NOT NULL");
    }

    private static void ConfigureHistory(ModelBuilder modelBuilder)
    {
        var history = modelBuilder.Entity<DealStageHistoryEntry>();
        history.ToTable("deal_stage_history", Schema, table =>
            table.HasCheckConstraint("CK_deal_stage_history_actor_kind", "actor_kind IN ('User','System')"));
        history.HasKey(value => value.Id);
        history.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        history.Property(value => value.TenantId).HasColumnName("tenant_id");
        history.Property(value => value.DealId).HasColumnName("deal_id");
        history.Property(value => value.PipelineId).HasColumnName("pipeline_id");
        // P4 del plan: el orden lo da el dominio (Deal.HistoryCount), no la base.
        history.Property(value => value.Ordinal).HasColumnName("ordinal");
        history.Property(value => value.FromStageId).HasColumnName("from_stage_id");
        history.Property(value => value.ToStageId).HasColumnName("to_stage_id");
        history.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        history.Property(value => value.ActorKind).HasColumnName("actor_kind").HasConversion<string>().HasMaxLength(8);
        history.Property(value => value.ActorMemberId).HasColumnName("actor_member_id");
        history.Property(value => value.Amount).HasColumnName("amount").HasPrecision(14, 2);
        history.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        history.Property(value => value.Probability).HasColumnName("probability");
        // Spec §4.3: el índice del historial por negocio y tiempo, para el forecast del slice 2.
        history.HasIndex(value => new { value.TenantId, value.DealId, value.OccurredAt }, "IX_deal_stage_history_deal");
        // P4 del plan: el orden de escritura. Único: dos escrituras concurrentes del mismo negocio leyeron el mismo HistoryCount y
        // proponen el mismo ordinal. Si el INSERT del historial sale antes que el UPDATE del negocio, choca acá y no en el token de
        // versión; PipelineUnitOfWork lo traduce igual a concurrency.conflict.
        history.HasIndex(value => new { value.TenantId, value.DealId, value.Ordinal }, DealHistoryOrdinalIndex).IsUnique();
    }

    private static void ConfigureDealQuotation(ModelBuilder modelBuilder)
    {
        var quotation = modelBuilder.Entity<DealQuotation>();
        quotation.ToTable("deal_quotations", Schema, table =>
            table.HasCheckConstraint("CK_deal_quotations_status", "status IN ('Sent','Converted')"));
        // §4.4: una cotización pertenece a lo sumo a un negocio.
        quotation.HasKey(value => new { value.TenantId, value.QuotationId });
        quotation.Property(value => value.TenantId).HasColumnName("tenant_id");
        quotation.Property(value => value.QuotationId).HasColumnName("quotation_id");
        quotation.Property(value => value.DealId).HasColumnName("deal_id");
        quotation.Property(value => value.CustomerId).HasColumnName("customer_id");
        quotation.Property(value => value.QuotationNumber).HasColumnName("quotation_number").HasMaxLength(DealQuotation.NumberMaxLength);
        quotation.Property(value => value.Total).HasColumnName("total").HasPrecision(14, 2);
        quotation.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        quotation.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        quotation.Property(value => value.OrderId).HasColumnName("order_id");
        quotation.Property(value => value.OrderCancelledAt).HasColumnName("order_cancelled_at");
        quotation.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        quotation.HasIndex(value => new { value.TenantId, value.DealId }, "IX_deal_quotations_deal");
        quotation.HasIndex(value => new { value.TenantId, value.CustomerId }, "IX_deal_quotations_customer");
    }

    private static void ConfigureDealOrder(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<DealOrder>();
        order.ToTable("deal_orders", Schema);
        // Spec 2026-10-15 §7: la clave es la idempotencia de order.created.
        order.HasKey(value => new { value.TenantId, value.OrderId });
        order.Property(value => value.TenantId).HasColumnName("tenant_id");
        order.Property(value => value.OrderId).HasColumnName("order_id");
        order.Property(value => value.DealId).HasColumnName("deal_id");
        order.Property(value => value.CustomerId).HasColumnName("customer_id");
        order.Property(value => value.OrderNumber).HasColumnName("order_number").HasMaxLength(DealOrder.NumberMaxLength);
        order.Property(value => value.Total).HasColumnName("total").HasPrecision(14, 2);
        order.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        order.Property(value => value.Source).HasColumnName("source").HasMaxLength(DealOrder.SourceMaxLength);
        order.Property(value => value.CancelledAt).HasColumnName("cancelled_at");
        order.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        order.HasIndex(value => new { value.TenantId, value.DealId }, "IX_deal_orders_deal");
        order.HasIndex(value => new { value.TenantId, value.CustomerId }, "IX_deal_orders_customer");
    }

    private static void ConfigureReason(ModelBuilder modelBuilder)
    {
        var reason = modelBuilder.Entity<PipelineReason>();
        reason.ToTable("reasons", Schema, table => table.HasCheckConstraint("CK_reasons_kind", "kind IN ('Loss','Dismiss')"));
        reason.HasKey(value => value.Id);
        reason.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        reason.Property(value => value.TenantId).HasColumnName("tenant_id");
        reason.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(8);
        reason.Property(value => value.Name).HasColumnName("name").HasMaxLength(PipelineReason.NameMaxLength);
        reason.Property(value => value.Position).HasColumnName("position");
        reason.Property(value => value.IsActive).HasColumnName("is_active");
        reason.Property(value => value.CreatedAt).HasColumnName("created_at");
        reason.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        reason.HasIndex(value => new { value.TenantId, value.Kind, value.Position }, "IX_reasons_tenant_kind");
    }

    private static void ConfigureTriageDecision(ModelBuilder modelBuilder)
    {
        var decision = modelBuilder.Entity<TriageDecision>();
        decision.ToTable("triage_decisions", Schema, table =>
        {
            table.HasCheckConstraint("CK_triage_decisions_kind", "kind IN ('Created','Dismissed')");
            table.HasCheckConstraint("CK_triage_decisions_actor_kind", "actor_kind IN ('User','System')");
        });
        decision.HasKey(value => value.Id);
        decision.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        decision.Property(value => value.TenantId).HasColumnName("tenant_id");
        decision.Property(value => value.ConversationId).HasColumnName("conversation_id");
        decision.Property(value => value.CustomerId).HasColumnName("customer_id");
        decision.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        decision.Property(value => value.DealId).HasColumnName("deal_id");
        decision.Property(value => value.DismissReasonId).HasColumnName("dismiss_reason_id");
        decision.Property(value => value.DecidedAt).HasColumnName("decided_at");
        decision.Property(value => value.DecidedAgainstInboundAt).HasColumnName("decided_against_inbound_at");
        decision.Property(value => value.ActorKind).HasColumnName("actor_kind").HasConversion<string>().HasMaxLength(8);
        decision.Property(value => value.ActorMemberId).HasColumnName("actor_member_id");
        // D38: una sola decisión por conversación y entrante. La segunda concurrente choca acá y PipelineUnitOfWork la traduce a
        // pipeline.triage.already_decided; el negocio creado en el mismo SaveChanges se revierte con ella. También sirve para
        // buscar las decisiones de una conversación.
        decision.HasIndex(value => new { value.TenantId, value.ConversationId, value.DecidedAgainstInboundAt }, TriageDecisionInboundIndex)
            .IsUnique();
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<PipelineInboxMessage>();
        inbox.ToTable("inbox_messages", Schema);
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Los nombres van a mano porque PipelineInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<PipelineOutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
    }
}
