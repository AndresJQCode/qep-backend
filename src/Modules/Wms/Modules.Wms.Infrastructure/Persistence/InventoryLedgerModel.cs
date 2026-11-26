using Microsoft.EntityFrameworkCore;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// El mapeo del ledger y los saldos (plan WMS-D, Task 3), aparte de <see cref="WmsDbContext"/> para que el contexto no crezca el
/// doble. Los enums van como texto y los decimales como <c>numeric(18,6)</c>, fijados a mano. <c>stock_movements</c> y
/// <c>stock_balances</c> no tienen <c>DbSet</c>: se escriben por SQL desde el ledger y nadie las toca con el change tracker. Las
/// FKs internas (spec §11.2) no están aquí: EF no puede declararlas con ids convertidos a <c>Guid</c> y las de
/// <c>stock_movements</c> caen sobre una tabla que EF no migra; las crea <c>InventoryLedgerSql.CreateInternalForeignKeys</c>.
/// </summary>
internal static class InventoryLedgerModel
{
    private const string Schema = WmsDbContext.Schema;

    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureStockMovement(modelBuilder);
        ConfigureStockBalance(modelBuilder);
        ConfigureLot(modelBuilder);
        ConfigureSerialUnit(modelBuilder);
        ConfigureReservation(modelBuilder);
        ConfigureOperationReceipt(modelBuilder);
        ConfigureBalanceCheck(modelBuilder);
        ConfigureImportPreview(modelBuilder);
        ConfigureAdjustment(modelBuilder);
        ConfigureAdjustmentLine(modelBuilder);
    }

    // La tabla la crea el SQL crudo de la migración (particionada por mes, con trigger): EF sólo conoce su forma.
    private static void ConfigureStockMovement(ModelBuilder modelBuilder)
    {
        var movement = modelBuilder.Entity<StockMovement>();
        movement.ToTable("stock_movements", Schema, table => table.ExcludeFromMigrations());
        movement.HasKey(value => new { value.Id, value.OccurredAt });
        movement.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        movement.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        movement.Property(value => value.TenantId).HasColumnName("tenant_id");
        movement.Property(value => value.OwnerId).HasColumnName("owner_id");
        movement.Property(value => value.VariantId).HasColumnName("variant_id");
        movement.Property(value => value.LotId).HasColumnName("lot_id");
        movement.Property(value => value.SerialUnitId).HasColumnName("serial_unit_id");
        movement.Property(value => value.FromLocationId).HasColumnName("from_location_id");
        movement.Property(value => value.FromStatus).HasColumnName("from_status").HasConversion<string>();
        movement.Property(value => value.FromHandlingUnitId).HasColumnName("from_handling_unit_id");
        movement.Property(value => value.ToLocationId).HasColumnName("to_location_id");
        movement.Property(value => value.ToStatus).HasColumnName("to_status").HasConversion<string>();
        movement.Property(value => value.ToHandlingUnitId).HasColumnName("to_handling_unit_id");
        movement.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(18, 6);
        movement.Property(value => value.CapturedUom).HasColumnName("captured_uom");
        movement.Property(value => value.CapturedQty).HasColumnName("captured_qty").HasPrecision(18, 6);
        movement.Property(value => value.ConversionFactor).HasColumnName("conversion_factor").HasPrecision(18, 6);
        movement.Property(value => value.Reason).HasColumnName("reason").HasConversion<string>();
        movement.Property(value => value.DocumentType).HasColumnName("document_type");
        movement.Property(value => value.DocumentId).HasColumnName("document_id");
        movement.Property(value => value.DocumentLineId).HasColumnName("document_line_id");
        movement.Property(value => value.BatchId).HasColumnName("batch_id");
        movement.Property(value => value.IdempotencyKey).HasColumnName("idempotency_key");
        movement.Property(value => value.ActorId).HasColumnName("actor_id");
        movement.Property(value => value.ActorType).HasColumnName("actor_type").HasConversion<string>();
        movement.Property(value => value.DeviceId).HasColumnName("device_id");
        movement.Property(value => value.RecordedAt).HasColumnName("recorded_at");
        movement.Property(value => value.ReversesMovementId).HasColumnName("reverses_movement_id");
        movement.Property(value => value.ReversesOccurredAt).HasColumnName("reverses_occurred_at");
        movement.Property(value => value.OverrideReason).HasColumnName("override_reason");
    }

    private static void ConfigureStockBalance(ModelBuilder modelBuilder)
    {
        var balance = modelBuilder.Entity<StockBalance>();
        balance.ToTable("stock_balances", Schema, table =>
        {
            // S1: el saldo negativo sólo lo admiten las virtuales; `allows_negative` es la copia que escribe el ledger.
            table.HasCheckConstraint(WmsDbContext.StockBalanceNonNegativeCheck, "on_hand >= 0 OR allows_negative");
            table.HasCheckConstraint(WmsDbContext.StockBalanceReservedCheck, "reserved >= 0 AND (allows_negative OR reserved <= on_hand)");
        });
        balance.HasKey(value => value.Id);
        balance.Property(value => value.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedOnAdd();
        balance.Property(value => value.TenantId).HasColumnName("tenant_id");
        balance.Property(value => value.OwnerId).HasColumnName("owner_id");
        balance.Property(value => value.WarehouseId).HasColumnName("warehouse_id");
        balance.Property(value => value.LocationId).HasColumnName("location_id");
        balance.Property(value => value.VariantId).HasColumnName("variant_id");
        balance.Property(value => value.LotId).HasColumnName("lot_id");
        balance.Property(value => value.HandlingUnitId).HasColumnName("handling_unit_id");
        balance.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        balance.Property(value => value.OnHand).HasColumnName("on_hand").HasPrecision(18, 6);
        balance.Property(value => value.Reserved).HasColumnName("reserved").HasPrecision(18, 6);
        balance.Property(value => value.AllowsNegative).HasColumnName("allows_negative");
        balance.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint");
        balance.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // NULLS NOT DISTINCT: dos saldos sin lote, o sueltos (sin unidad, D-12), de la misma clave son el mismo saldo. La unidad va entre
        // el lote y el estado, como en StockKeyComparer.
        balance.HasIndex(value => new
            {
                value.TenantId, value.OwnerId, value.LocationId, value.VariantId, value.LotId, value.HandlingUnitId, value.Status,
            })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName(WmsDbContext.StockBalanceKeyIndex);
        // §11.2: por variante y estado (probe de variante, resumen), por ubicación (probe de ubicación, borrado) y, desde GET /stock, por
        // (tenant, bodega, variante): la pantalla de saldos filtra por bodega y sin este índice recorre todos los saldos del tenant.
        balance.HasIndex(value => new { value.TenantId, value.VariantId, value.Status }, "IX_stock_balances_tenant_variant_status");
        balance.HasIndex(value => new { value.TenantId, value.WarehouseId, value.VariantId }, "IX_stock_balances_tenant_warehouse_variant");
        balance.HasIndex(value => value.LocationId, "IX_stock_balances_location");
    }

    private static void ConfigureLot(ModelBuilder modelBuilder)
    {
        var lot = modelBuilder.Entity<Lot>();
        lot.ToTable("lots", Schema);
        lot.HasKey(value => value.Id);
        lot.Property(value => value.Id).HasColumnName("id").HasConversion(id => id.Value, value => new LotId(value)).ValueGeneratedNever();
        lot.Property(value => value.TenantId).HasColumnName("tenant_id");
        lot.Property(value => value.OwnerId).HasColumnName("owner_id");
        lot.Property(value => value.VariantId).HasColumnName("variant_id");
        lot.Property(value => value.Code).HasColumnName("code").HasMaxLength(Lot.CodeMaxLength);
        lot.Property(value => value.ExpirationDate).HasColumnName("expiration_date");
        lot.Property(value => value.ManufacturedAt).HasColumnName("manufactured_at");
        lot.Property(value => value.CreatedAt).HasColumnName("created_at");
        lot.HasIndex(value => new { value.TenantId, value.OwnerId, value.VariantId, value.Code })
            .IsUnique()
            .HasDatabaseName(WmsDbContext.LotCodeIndex);
    }

    private static void ConfigureSerialUnit(ModelBuilder modelBuilder)
    {
        var serial = modelBuilder.Entity<SerialUnit>();
        serial.ToTable("serial_units", Schema);
        serial.HasKey(value => value.Id);
        serial.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        serial.Property(value => value.TenantId).HasColumnName("tenant_id");
        serial.Property(value => value.OwnerId).HasColumnName("owner_id");
        serial.Property(value => value.VariantId).HasColumnName("variant_id");
        serial.Property(value => value.Serial).HasColumnName("serial").HasMaxLength(128);
        serial.Property(value => value.CurrentLocationId).HasColumnName("current_location_id");
        serial.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        serial.Property(value => value.LotId)
            .HasColumnName("lot_id")
            .HasConversion(id => id!.Value.Value, value => new LotId(value));
        serial.HasIndex(value => new { value.TenantId, value.VariantId, value.Serial })
            .IsUnique()
            .HasDatabaseName(WmsDbContext.SerialUnitIndex);
        serial.HasIndex(value => value.CurrentLocationId, "IX_serial_units_location");
    }

    private static void ConfigureReservation(ModelBuilder modelBuilder)
    {
        var reservation = modelBuilder.Entity<Reservation>();
        reservation.ToTable("reservations", Schema, table =>
        {
            table.HasCheckConstraint("CK_reservations_status", "status IN ('Open','Consumed','Released','Expired')");
            table.HasCheckConstraint("CK_reservations_quantity", "quantity > 0");
        });
        reservation.HasKey(value => value.Id);
        reservation.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        reservation.Property(value => value.TenantId).HasColumnName("tenant_id");
        reservation.Property(value => value.OwnerId).HasColumnName("owner_id");
        reservation.Property(value => value.VariantId).HasColumnName("variant_id");
        reservation.Property(value => value.WarehouseId).HasColumnName("warehouse_id");
        reservation.Property(value => value.LocationId).HasColumnName("location_id");
        reservation.Property(value => value.LotId)
            .HasColumnName("lot_id")
            .HasConversion(id => id!.Value.Value, value => new LotId(value));
        reservation.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(18, 6);
        reservation.Property(value => value.DemandType).HasColumnName("demand_type").HasMaxLength(32);
        reservation.Property(value => value.DemandId).HasColumnName("demand_id");
        reservation.Property(value => value.DemandLineId).HasColumnName("demand_line_id");
        reservation.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        reservation.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        reservation.Property(value => value.CreatedAt).HasColumnName("created_at");
        // Sólo las abiertas pesan en el saldo: el índice parcial sirve el cálculo de `reserved` por clave.
        reservation.HasIndex(
                value => new { value.TenantId, value.OwnerId, value.LocationId, value.VariantId, value.LotId, value.Status },
                WmsDbContext.ReservationOpenIndex)
            .HasFilter("status = 'Open'");
        reservation.HasIndex(value => new { value.DemandType, value.DemandId }, "IX_reservations_demand");
    }

    private static void ConfigureOperationReceipt(ModelBuilder modelBuilder)
    {
        var receipt = modelBuilder.Entity<OperationReceipt>();
        receipt.ToTable("operation_receipts", Schema);
        receipt.HasKey(value => new { value.TenantId, value.IdempotencyKey }).HasName(WmsDbContext.OperationReceiptPrimaryKey);
        receipt.Property(value => value.TenantId).HasColumnName("tenant_id");
        receipt.Property(value => value.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(OperationReceipt.KeyMaxLength);
        receipt.Property(value => value.RequestHash).HasColumnName("request_hash").HasColumnType("char(64)");
        receipt.Property(value => value.ResponseJson).HasColumnName("response_json").HasColumnType("jsonb");
        receipt.Property(value => value.CreatedAt).HasColumnName("created_at");
        // La purga a 45 días (S13) recorre por fecha.
        receipt.HasIndex(value => value.CreatedAt, "IX_operation_receipts_created");
    }

    private static void ConfigureBalanceCheck(ModelBuilder modelBuilder)
    {
        var check = modelBuilder.Entity<BalanceCheck>();
        check.ToTable("balance_checks", Schema);
        check.HasKey(value => value.Id);
        check.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        check.Property(value => value.TenantId).HasColumnName("tenant_id");
        check.Property(value => value.WarehouseId).HasColumnName("warehouse_id");
        check.Property(value => value.OwnerId).HasColumnName("owner_id");
        check.Property(value => value.LocationId).HasColumnName("location_id");
        check.Property(value => value.VariantId).HasColumnName("variant_id");
        check.Property(value => value.LotId).HasColumnName("lot_id");
        check.Property(value => value.HandlingUnitId).HasColumnName("handling_unit_id");
        check.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        check.Property(value => value.LedgerOnHand).HasColumnName("ledger_on_hand").HasPrecision(18, 6);
        check.Property(value => value.BalanceOnHand).HasColumnName("balance_on_hand").HasPrecision(18, 6);
        check.Property(value => value.LastMovementId).HasColumnName("last_movement_id");
        check.Property(value => value.CheckedAt).HasColumnName("checked_at");
        check.HasIndex(value => new { value.TenantId, value.CheckedAt }, "IX_balance_checks_tenant_checked");
    }

    private static void ConfigureImportPreview(ModelBuilder modelBuilder)
    {
        var preview = modelBuilder.Entity<ImportPreview>();
        preview.ToTable("import_previews", Schema);
        preview.HasKey(value => value.Id);
        preview.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        preview.Property(value => value.TenantId).HasColumnName("tenant_id");
        preview.Property(value => value.RequestedBy).HasColumnName("requested_by");
        preview.Property(value => value.WarehouseId)
            .HasColumnName("warehouse_id")
            .HasConversion(id => id.Value, value => new WarehouseId(value));
        preview.Property(value => value.OwnerId)
            .HasColumnName("owner_id")
            .HasConversion(id => id.Value, value => new OwnerId(value));
        preview.Property(value => value.RowsJson).HasColumnName("rows_json").HasColumnType("jsonb");
        preview.Property(value => value.CanCommit).HasColumnName("can_commit");
        preview.Property(value => value.CreatedAt).HasColumnName("created_at");
        preview.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        preview.HasIndex(value => value.ExpiresAt, "IX_import_previews_expires");
    }

    private static void ConfigureAdjustment(ModelBuilder modelBuilder)
    {
        var adjustment = modelBuilder.Entity<Adjustment>();
        adjustment.ToTable("adjustments", Schema, table =>
        {
            table.HasCheckConstraint(
                "CK_adjustments_reason",
                "reason IN ('InitialLoad','CountVariance','Damage','Loss','Found','Correction','Other')");
            table.HasCheckConstraint("CK_adjustments_status", "status IN ('Proposed','Approved','Posted','Rejected')");
        });
        adjustment.HasKey(value => value.Id);
        adjustment.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        adjustment.Property(value => value.TenantId).HasColumnName("tenant_id");
        adjustment.Property(value => value.OwnerId)
            .HasColumnName("owner_id")
            .HasConversion(id => id.Value, value => new OwnerId(value));
        adjustment.Property(value => value.WarehouseId)
            .HasColumnName("warehouse_id")
            .HasConversion(id => id.Value, value => new WarehouseId(value));
        adjustment.Property(value => value.Number).HasColumnName("number").HasMaxLength(32);
        adjustment.Property(value => value.Reason).HasColumnName("reason").HasConversion<string>().HasMaxLength(16);
        adjustment.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        adjustment.Property(value => value.Note).HasColumnName("note").HasMaxLength(500);
        adjustment.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        adjustment.Property(value => value.ProposedBy).HasColumnName("proposed_by");
        adjustment.Property(value => value.ProposedAt).HasColumnName("proposed_at");
        adjustment.Property(value => value.ApprovedBy).HasColumnName("approved_by");
        adjustment.Property(value => value.ApprovedAt).HasColumnName("approved_at");
        adjustment.Property(value => value.PostedAt).HasColumnName("posted_at");
        adjustment.Property(value => value.RejectedAt).HasColumnName("rejected_at");
        adjustment.Property(value => value.RejectedBy).HasColumnName("rejected_by");
        adjustment.Property(value => value.RejectNote).HasColumnName("reject_note").HasMaxLength(500);
        adjustment.Property(value => value.BatchId).HasColumnName("batch_id");
        adjustment.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        adjustment.HasIndex(value => new { value.TenantId, value.Number }).IsUnique().HasDatabaseName(WmsDbContext.AdjustmentNumberIndex);
        adjustment.HasIndex(value => new { value.TenantId, value.Status, value.WarehouseId }, "IX_adjustments_tenant_status_warehouse");
        adjustment.HasMany(value => value.Lines)
            .WithOne()
            .HasForeignKey("AdjustmentId")
            .HasConstraintName("FK_adjustment_lines_adjustment")
            .OnDelete(DeleteBehavior.Cascade);
        adjustment.Navigation(value => value.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureAdjustmentLine(ModelBuilder modelBuilder)
    {
        var line = modelBuilder.Entity<AdjustmentLine>();
        line.ToTable("adjustment_lines", Schema);
        line.HasKey(value => value.Id);
        line.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        line.Property<Guid>("AdjustmentId").HasColumnName("adjustment_id");
        line.Property(value => value.LocationId)
            .HasColumnName("location_id")
            .HasConversion(id => id.Value, value => new LocationId(value));
        line.Property(value => value.VariantId).HasColumnName("variant_id");
        line.Property(value => value.LotId)
            .HasColumnName("lot_id")
            .HasConversion(id => id!.Value.Value, value => new LotId(value));
        line.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        line.Property(value => value.QuantityDelta).HasColumnName("quantity_delta").HasPrecision(18, 6);
        line.Property(value => value.CapturedUom).HasColumnName("captured_uom").HasMaxLength(32);
        line.Property(value => value.CapturedQty).HasColumnName("captured_qty").HasPrecision(18, 6);
        line.Property(value => value.Position).HasColumnName("position");
        line.HasIndex("AdjustmentId", nameof(AdjustmentLine.Position)).IsUnique().HasDatabaseName("IX_adjustment_lines_position");
    }
}
