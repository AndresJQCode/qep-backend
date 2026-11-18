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
        order.Property(value => value.Currency).HasColumnName("currency").HasColumnType("char(3)");
        order.Property(value => value.Notes).HasColumnName("notes").HasMaxLength(PurchaseOrder.NotesMaxLength);
        order.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        order.Property(value => value.ConfirmedWithChanges).HasColumnName("confirmed_with_changes");
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
        line.HasIndex(OrderIdColumn, LineOrdinal).HasDatabaseName("IX_purchase_order_lines_order_ordinal");
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
