using Microsoft.EntityFrameworkCore;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>El mapeo del motor de tareas (spec 2026-11-26 §6.1). Mismo criterio que <see cref="InventoryLedgerModel"/>: enums como
/// texto, <c>version</c> como bigint y cantidades <c>numeric(18,6)</c>, fijados a mano. Aquí las FKs sí las declara EF: las tareas
/// referencian bodega, dueño, ubicación y lote con los mismos tipos de id que esas tablas.</summary>
internal static class WarehouseTaskModel
{
    private const string Schema = WmsDbContext.Schema;

    private const int EnumMaxLength = 16;

    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureTask(modelBuilder);
        ConfigureLine(modelBuilder);
        ConfigureEvent(modelBuilder);
    }

    // Los CHECK repiten lo que el dominio ya garantiza, para que la base no dependa de quien escribe. Un tipo o un estado nuevo
    // necesita su migración.
    private static void ConfigureTask(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<WarehouseTask>();
        task.ToTable("tasks", Schema, table =>
        {
            table.HasCheckConstraint("CK_tasks_type", "type IN ('Move')");
            table.HasCheckConstraint(
                "CK_tasks_status", "status IN ('Planned','Ready','Assigned','InProgress','Done','Cancelled')");
            table.HasCheckConstraint("CK_tasks_priority", "priority BETWEEN 1 AND 9");
            table.HasCheckConstraint(
                "CK_tasks_claim",
                "status <> 'InProgress' OR (claimed_by_member_id IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");
            table.HasCheckConstraint("CK_tasks_assigned", "status <> 'Assigned' OR assigned_member_id IS NOT NULL");
            table.HasCheckConstraint(
                "CK_tasks_released", "status NOT IN ('Ready','Assigned','InProgress') OR released_at IS NOT NULL");
        });
        task.HasKey(value => value.Id);
        task.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        task.Property(value => value.TenantId).HasColumnName("tenant_id");
        task.Property(value => value.WarehouseId)
            .HasColumnName("warehouse_id")
            .HasConversion(id => id.Value, value => new WarehouseId(value));
        task.Property(value => value.OwnerId)
            .HasColumnName("owner_id")
            .HasConversion(id => id.Value, value => new OwnerId(value));
        task.Property(value => value.Number).HasColumnName("number").HasMaxLength(32);
        task.Property(value => value.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(EnumMaxLength);
        task.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(EnumMaxLength);
        task.Property(value => value.Priority).HasColumnName("priority").HasConversion<short>();
        task.Property(value => value.Urgent).HasColumnName("urgent");
        task.Property(value => value.DueAt).HasColumnName("due_at");
        task.Property(value => value.ReleasedAt).HasColumnName("released_at");
        task.Property(value => value.SourceType).HasColumnName("source_type").HasConversion<string>().HasMaxLength(32);
        task.Property(value => value.SourceId).HasColumnName("source_id");
        task.Property(value => value.Note).HasColumnName("note").HasMaxLength(WarehouseTask.NoteMaxLength);
        task.Property(value => value.AssignedMemberId).HasColumnName("assigned_member_id");
        task.Property(value => value.ClaimedByMemberId).HasColumnName("claimed_by_member_id");
        task.Property(value => value.ClaimedDeviceId).HasColumnName("claimed_device_id").HasMaxLength(WarehouseTask.DeviceIdMaxLength);
        task.Property(value => value.LeaseToken).HasColumnName("lease_token");
        task.Property(value => value.LeaseExpiresAt).HasColumnName("lease_expires_at");
        task.Property(value => value.LastActivityAt).HasColumnName("last_activity_at");
        task.Property(value => value.CreatedBy).HasColumnName("created_by");
        task.Property(value => value.CreatedAt).HasColumnName("created_at");
        task.Property(value => value.CompletedAt).HasColumnName("completed_at");
        task.Property(value => value.CancelledAt).HasColumnName("cancelled_at");
        task.Property(value => value.CancelReason).HasColumnName("cancel_reason").HasMaxLength(WarehouseTask.ReasonMaxLength);
        task.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();

        // Con nombre en el HasIndex: dos índices sobre las mismas propiedades se fusionan si sólo se distinguen por HasDatabaseName.
        task.HasIndex(value => new { value.TenantId, value.Number }).IsUnique().HasDatabaseName(WmsDbContext.TaskNumberIndex);
        // El pool que recorre «siguiente tarea»: sólo lo reclamable.
        task.HasIndex(value => new { value.TenantId, value.WarehouseId, value.Status }, "IX_tasks_pool")
            .HasFilter("status IN ('Ready','Assigned')");
        // Una tarea en curso por operario (DA-08): la última red detrás del lock del handler.
        task.HasIndex(value => new { value.TenantId, value.ClaimedByMemberId }, WmsDbContext.TaskMemberInProgressIndex)
            .IsUnique()
            .HasFilter("status = 'InProgress'");
        // El barrido de leases vencidos.
        task.HasIndex(value => value.LeaseExpiresAt, "IX_tasks_lease").HasFilter("status = 'InProgress'");
        task.HasIndex(value => new { value.TenantId, value.SourceType, value.SourceId }, "IX_tasks_source");
        // Los conteos del tablero (por estado y abiertas por tipo) salen sólo del índice; ListAssignedOpenAsync lo usa para acotar
        // por bodega y estado, pero sí lee la tabla para traer el asignado (revisión T1, M4).
        task.HasIndex(value => new { value.TenantId, value.WarehouseId, value.Status, value.Type }, "IX_tasks_board");
        // El listado del supervisor: la página sale del índice en el orden de ListAsync.
        task.HasIndex(value => new { value.TenantId, value.WarehouseId, value.CreatedAt, value.Number }, "IX_tasks_list")
            .IsDescending(false, false, true, true);

        task.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(value => value.WarehouseId)
            .HasConstraintName("FK_tasks_warehouse")
            .OnDelete(DeleteBehavior.Restrict);
        task.HasOne<Owner>()
            .WithMany()
            .HasForeignKey(value => value.OwnerId)
            .HasConstraintName("FK_tasks_owner")
            .OnDelete(DeleteBehavior.Restrict);

        // La línea no conoce a su tarea: la clave es sombra y el dominio no la ve.
        task.HasMany(value => value.Lines)
            .WithOne()
            .HasForeignKey("TaskId")
            .HasConstraintName("FK_task_lines_task")
            .OnDelete(DeleteBehavior.Cascade);
        task.Navigation(value => value.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        task.HasMany(value => value.Events)
            .WithOne()
            .HasForeignKey(value => value.TaskId)
            .HasConstraintName("FK_task_events_task")
            .OnDelete(DeleteBehavior.Restrict);
        task.Navigation(value => value.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureLine(ModelBuilder modelBuilder)
    {
        var line = modelBuilder.Entity<WarehouseTaskLine>();
        line.ToTable("task_lines", Schema, table =>
        {
            table.HasCheckConstraint("CK_task_lines_status", "line_status IN ('Open','Done','Short','Cancelled')");
            table.HasCheckConstraint(
                "CK_task_lines_quantity",
                "quantity_planned > 0 AND quantity_confirmed >= 0 AND quantity_confirmed <= quantity_planned");
        });
        line.HasKey(value => value.Id);
        // El id lo pone el dominio: así EF inserta lo que aparece nuevo en la colección en vez de intentar actualizarlo.
        line.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        line.Property<Guid>("TaskId").HasColumnName("task_id");
        line.Property(value => value.Position).HasColumnName("position");
        line.Property(value => value.VariantId).HasColumnName("variant_id");
        line.Property(value => value.LotId)
            .HasColumnName("lot_id")
            .HasConversion(id => id!.Value.Value, value => new LotId(value));
        line.Property(value => value.StockStatus).HasColumnName("stock_status").HasConversion<string>().HasMaxLength(EnumMaxLength);
        line.Property(value => value.FromLocationId)
            .HasColumnName("from_location_id")
            .HasConversion(id => id.Value, value => new LocationId(value));
        line.Property(value => value.ToLocationId)
            .HasColumnName("to_location_id")
            .HasConversion(id => id!.Value.Value, value => new LocationId(value));
        line.Property(value => value.QuantityPlanned).HasColumnName("quantity_planned").HasPrecision(18, 6);
        line.Property(value => value.QuantityConfirmed).HasColumnName("quantity_confirmed").HasPrecision(18, 6);
        // `status` es de la tarea: en la línea la columna se llama line_status (spec §6.1).
        line.Property(value => value.Status).HasColumnName("line_status").HasConversion<string>().HasMaxLength(EnumMaxLength);
        line.Property(value => value.CancelReason).HasColumnName("cancel_reason").HasMaxLength(WarehouseTask.ReasonMaxLength);
        line.Ignore(value => value.Remaining);
        line.HasIndex("TaskId", nameof(WarehouseTaskLine.Position)).IsUnique().HasDatabaseName("IX_task_lines_position");

        line.HasOne<Location>()
            .WithMany()
            .HasForeignKey(value => value.FromLocationId)
            .HasConstraintName("FK_task_lines_from_location")
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne<Location>()
            .WithMany()
            .HasForeignKey(value => value.ToLocationId)
            .HasConstraintName("FK_task_lines_to_location")
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne<Lot>()
            .WithMany()
            .HasForeignKey(value => value.LotId)
            .HasConstraintName("FK_task_lines_lot")
            .OnDelete(DeleteBehavior.Restrict);
    }

    // Sólo se insertan: es la historia de la tarea.
    private static void ConfigureEvent(ModelBuilder modelBuilder)
    {
        var history = modelBuilder.Entity<WarehouseTaskEvent>();
        history.ToTable("task_events", Schema);
        history.HasKey(value => value.Id);
        history.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        history.Property(value => value.TenantId).HasColumnName("tenant_id");
        history.Property(value => value.TaskId).HasColumnName("task_id");
        history.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        history.Property(value => value.MemberId).HasColumnName("member_id");
        history.Property(value => value.DeviceId).HasColumnName("device_id").HasMaxLength(WarehouseTask.DeviceIdMaxLength);
        history.Property(value => value.LineId).HasColumnName("line_id");
        history.Property(value => value.Quantity).HasColumnName("quantity").HasPrecision(18, 6);
        history.Property(value => value.Detail).HasColumnName("detail").HasMaxLength(WarehouseTask.ReasonMaxLength);
        history.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        // El default sólo existe para el rollout: un pod viejo inserta sin la columna. El código nuevo siempre la escribe.
        history.Property(value => value.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()");
        history.HasIndex(value => new { value.TaskId, value.OccurredAt }, "IX_task_events_task");
    }
}
