using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Infrastructure.Persistence;

public sealed class PurchasesDbContext(DbContextOptions<PurchasesDbContext> options)
    : DbContext(options)
{
    public const string Schema = "purchases";

    /// <summary>Único del consecutivo: con el contador no debería chocar nunca; si choca es un bug, no un 422.</summary>
    public const string OrderNumberIndex = "IX_purchase_orders_tenant_number";

    /// <summary>
    /// Posición de la línea en el cuerpo. El dominio no la guarda (la lista ya está en orden); vive como columna sombra y la
    /// pone <see cref="StampLineOrdinals"/> antes de cada guardado.
    /// </summary>
    internal const string LineOrdinal = "Ordinal";

    private const string OrderIdColumn = "OrderId";

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    internal DbSet<PurchaseOrderNumberCounter> OrderNumberCounters => Set<PurchaseOrderNumberCounter>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // Mayorista fase 3: el inbox del consumidor de los eventos del mayorista y la proyección de sólo lectura del outbox de
    // plataforma (ExcludeFromMigrations), como Pipeline.
    internal DbSet<PurchasesInboxMessage> Inbox => Set<PurchasesInboxMessage>();

    internal DbSet<PurchasesOutboxRecord> Outbox => Set<PurchasesOutboxRecord>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampLineOrdinals();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampLineOrdinals();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureOrder(modelBuilder);
        ConfigureLine(modelBuilder);
        ConfigureCounter(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    // Las líneas de una orden van en el orden del cuerpo: el índice en la lista del agregado es la posición que se guarda.
    private void StampLineOrdinals()
    {
        ChangeTracker.DetectChanges();
        foreach (var order in ChangeTracker.Entries<PurchaseOrder>().Where(entry => entry.State != EntityState.Deleted))
        {
            var lines = order.Entity.Lines;
            for (var index = 0; index < lines.Count; index++)
            {
                var ordinal = Entry(lines[index]).Property<int>(LineOrdinal);
                if (ordinal.CurrentValue != index)
                {
                    ordinal.CurrentValue = index;
                }
            }
        }
    }

    // El estado va como texto de 16 y `version` como bigint fijado a mano: un cambio del tipo CLR no cambia el esquema.
    // Proveedor, variante y membresías son referencias blandas: viven en otros esquemas y no llevan FK.
    private static void ConfigureOrder(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<PurchaseOrder>();
        order.ToTable("purchase_orders", Schema);
        order.HasKey(value => value.Id);
        order.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PurchaseOrderId(value))
            .ValueGeneratedNever();
        order.Property(value => value.TenantId).HasColumnName("tenant_id");
        order.Property(value => value.Number).HasColumnName("number").HasMaxLength(20);
        order.Property(value => value.SupplierId).HasColumnName("supplier_id");
        // Mayorista fase 3: el modo vinculado (D-10) y el pedido que la orden creó en el mayorista. Referencias blandas a otro
        // tenant: sin FK.
        order.Property(value => value.SupplierTenantId).HasColumnName("supplier_tenant_id");
        order.Property(value => value.LinkedOrderId).HasColumnName("linked_order_id");
        order.Property(value => value.LinkedOrderNumber).HasColumnName("linked_order_number").HasMaxLength(64);
        order.Property(value => value.Currency).HasColumnName("currency").HasColumnType("char(3)");
        order.Property(value => value.Notes).HasColumnName("notes").HasMaxLength(PurchaseOrder.NotesMaxLength);
        order.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        order.Property(value => value.ConfirmedWithChanges).HasColumnName("confirmed_with_changes");
        // Expand/contract: columnas nulables; un pod anterior ni las lee ni las escribe.
        order.Property(value => value.LastEmailStatus)
            .HasColumnName("last_email_status")
            .HasConversion<string>()
            .HasMaxLength(16);
        order.Property(value => value.LastEmailTo).HasColumnName("last_email_to").HasMaxLength(254);
        order.Property(value => value.LastEmailAt).HasColumnName("last_email_at");
        order.Property(value => value.CreatedByMemberId).HasColumnName("created_by_member_id");
        order.Property(value => value.CreatedAt).HasColumnName("created_at");
        order.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        order.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();

        order.HasIndex(value => new { value.TenantId, value.Number })
            .IsUnique()
            .HasDatabaseName(OrderNumberIndex);
        // El orden de la lista (D-13): created_at DESC, id DESC.
        order.HasIndex(value => new { value.TenantId, value.CreatedAt, value.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_purchase_orders_tenant_created");
        order.HasIndex(value => new { value.TenantId, value.Status }).HasDatabaseName("IX_purchase_orders_tenant_status");
        order.HasIndex(value => new { value.TenantId, value.SupplierId }).HasDatabaseName("IX_purchase_orders_tenant_supplier");
        // Mayorista fase 3: la orden de un pedido del mayorista. Parcial: las órdenes externas y los borradores no lo tienen.
        order.HasIndex(value => new { value.TenantId, value.LinkedOrderId })
            .HasFilter("linked_order_id IS NOT NULL")
            .HasDatabaseName("IX_purchase_orders_tenant_linked_order");

        order.HasMany(value => value.Lines)
            .WithOne()
            .HasForeignKey(OrderIdColumn)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
        order.Navigation(value => value.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // El historial no tiene identidad propia: la orden y la posición. Owned, así viaja siempre con la orden.
        order.OwnsMany(value => value.History, history =>
        {
            history.ToTable("purchase_order_history", Schema);
            history.WithOwner().HasForeignKey(OrderIdColumn);
            history.Property<PurchaseOrderId>(OrderIdColumn)
                .HasColumnName("order_id")
                .HasConversion(id => id.Value, value => new PurchaseOrderId(value));
            history.Property(entry => entry.Sequence).HasColumnName("sequence").ValueGeneratedNever();
            history.HasKey(OrderIdColumn, nameof(PurchaseOrderHistoryEntry.Sequence));
            history.Property(entry => entry.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16);
            history.Property(entry => entry.OccurredAt).HasColumnName("occurred_at");
            history.Property(entry => entry.MemberId).HasColumnName("member_id");
        });
        order.Navigation(value => value.History).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureLine(ModelBuilder modelBuilder)
    {
        var line = modelBuilder.Entity<PurchaseOrderLine>();
        line.ToTable("purchase_order_lines", Schema);
        line.HasKey(value => value.Id);
        line.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        line.Property<PurchaseOrderId>(OrderIdColumn)
            .HasColumnName("order_id")
            .HasConversion(id => id.Value, value => new PurchaseOrderId(value));
        line.Property<int>(LineOrdinal).HasColumnName("ordinal");
        line.Property(value => value.VariantId).HasColumnName("variant_id");
        line.Property(value => value.Code).HasColumnName("code").HasMaxLength(64);
        line.Property(value => value.Name).HasColumnName("name").HasMaxLength(300);
        line.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(10, 2);
        line.Property(value => value.UnitCost).HasColumnName("unit_cost").HasPrecision(14, 2);
        line.Property(value => value.ConfirmedQuantity).HasColumnName("confirmed_quantity").HasPrecision(10, 2);
        line.Property(value => value.ConfirmedUnitCost).HasColumnName("confirmed_unit_cost").HasPrecision(14, 2);
        line.Property(value => value.TaxRatePercent).HasColumnName("tax_rate_percent").HasPrecision(5, 2);
        // Mayorista fase 3: la variante del mayorista y la foto de su lista (D-9), IVA incluido. VariantId y Quantity pasan a
        // nulables (una línea vinculada no tiene variante propia hasta confirmar; una agregada por el mayorista no se pidió).
        line.Property(value => value.SupplierVariantId).HasColumnName("supplier_variant_id");
        line.Property(value => value.ListBasePrice).HasColumnName("list_base_price").HasPrecision(14, 2);
        line.Property(value => value.ListDiscountPercent).HasColumnName("list_discount_percent").HasPrecision(5, 2);
        line.Property(value => value.ListUnitPrice).HasColumnName("list_unit_price").HasPrecision(14, 2);
        // Las filas que ya existen quedan en false: la migración agrega la columna con ese default.
        line.Property(value => value.CreatedInCatalog).HasColumnName("created_in_catalog");
        line.HasIndex(OrderIdColumn, LineOrdinal).HasDatabaseName("IX_purchase_order_lines_order_ordinal");
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<PurchasesInboxMessage>();
        inbox.ToTable("inbox_messages", Schema);
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Los nombres van a mano porque PurchasesInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<PurchasesOutboxRecord>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name");
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        // Columnas que sólo escribe PurchaseOrderEventPublisher; el consumidor mayorista no las lee.
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
        outbox.Property(value => value.LastError).HasColumnName("last_error");
    }

    private static void ConfigureCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<PurchaseOrderNumberCounter>();
        counter.ToTable("order_number_counters", Schema);
        counter.HasKey(value => value.TenantId);
        counter.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        counter.Property(value => value.NextValue).HasColumnName("next_value").HasColumnType("bigint");
    }
}

/// <summary>Una fila por tenant con el siguiente consecutivo. Sólo la escribe <see cref="PurchaseOrderNumberGenerator"/> por SQL.</summary>
internal sealed class PurchaseOrderNumberCounter
{
    public Guid TenantId { get; set; }

    public long NextValue { get; set; }
}
