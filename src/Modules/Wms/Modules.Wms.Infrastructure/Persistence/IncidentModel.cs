using Microsoft.EntityFrameworkCore;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>El mapeo de las novedades (spec 2026-11-26 §8.1). Mismo criterio que <see cref="WarehouseTaskModel"/>: enums como texto,
/// <c>version</c> como bigint y cantidades <c>numeric(18,6)</c>, fijados a mano. La tarea, la línea, el ajuste y los archivos de
/// evidencia se nombran por <see cref="Guid"/> sin FK (como <c>tasks.source_id</c>): son de otras áreas o de otro módulo.</summary>
internal static class IncidentModel
{
    private const string Schema = WmsDbContext.Schema;

    private const int EnumMaxLength = 32;

    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureIncident(modelBuilder);
        ConfigureEvidence(modelBuilder);
        ConfigureEvent(modelBuilder);
    }

    // Los CHECK repiten lo que el dominio ya garantiza, para que la base no dependa de quien escribe. Un tipo, un estado o una
    // resolución nuevos necesitan su migración; el vocabulario de eventos no (no tiene CHECK).
    private static void ConfigureIncident(ModelBuilder modelBuilder)
    {
        var incident = modelBuilder.Entity<Incident>();
        incident.ToTable("incidents", Schema, table =>
        {
            table.HasCheckConstraint(
                "CK_incidents_type", "type IN ('ShortMove','LocationMismatch','UnreadableLabel','CountVariance','SyncConflict')");
            table.HasCheckConstraint("CK_incidents_status", "status IN ('Open','InReview','Resolved','Rejected')");
            table.HasCheckConstraint(
                "CK_incidents_resolution", "resolution IS NULL OR resolution IN ('Accept','AcceptIntoStock')");
            table.HasCheckConstraint(
                "CK_incidents_resolved", "status <> 'Resolved' OR (resolution IS NOT NULL AND resolved_at IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_incidents_quantities", "(expected_qty IS NULL OR expected_qty >= 0) AND (actual_qty IS NULL OR actual_qty >= 0)");
        });
        incident.HasKey(value => value.Id);
        incident.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        incident.Property(value => value.TenantId).HasColumnName("tenant_id");
        incident.Property(value => value.WarehouseId)
            .HasColumnName("warehouse_id")
            .HasConversion(id => id.Value, value => new WarehouseId(value));
        incident.Property(value => value.OwnerId)
            .HasColumnName("owner_id")
            .HasConversion(id => id.Value, value => new OwnerId(value));
        incident.Property(value => value.Number).HasColumnName("number").HasMaxLength(32);
        incident.Property(value => value.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(EnumMaxLength);
        incident.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(EnumMaxLength);
        incident.Property(value => value.SourceTaskId).HasColumnName("source_task_id");
        incident.Property(value => value.SourceTaskLineId).HasColumnName("source_task_line_id");
        incident.Property(value => value.VerificationTaskId).HasColumnName("verification_task_id");
        incident.Property(value => value.LocationId)
            .HasColumnName("location_id")
            .HasConversion(id => id!.Value.Value, value => new LocationId(value));
        incident.Property(value => value.VariantId).HasColumnName("variant_id");
        incident.Property(value => value.LotId)
            .HasColumnName("lot_id")
            .HasConversion(id => id!.Value.Value, value => new LotId(value));
        incident.Property(value => value.StockStatus)
            .HasColumnName("stock_status")
            .HasConversion<string>()
            .HasMaxLength(EnumMaxLength);
        incident.Property(value => value.ExpectedQty).HasColumnName("expected_qty").HasPrecision(18, 6);
        incident.Property(value => value.ActualQty).HasColumnName("actual_qty").HasPrecision(18, 6);
        incident.Property(value => value.RawCode).HasColumnName("raw_code").HasMaxLength(Incident.RawCodeMaxLength);
        incident.Property(value => value.RawPayload).HasColumnName("raw_payload").HasColumnType("jsonb");
        incident.Property(value => value.Note).HasColumnName("note").HasMaxLength(Incident.NoteMaxLength);
        incident.Property(value => value.ReportedBy).HasColumnName("reported_by");
        incident.Property(value => value.DeviceId).HasColumnName("device_id").HasMaxLength(WarehouseTask.DeviceIdMaxLength);
        incident.Property(value => value.ReportedAt).HasColumnName("reported_at");
        incident.Property(value => value.OperationKey).HasColumnName("operation_key").HasMaxLength(OperationReceipt.KeyMaxLength);
        incident.Property(value => value.Resolution)
            .HasColumnName("resolution")
            .HasConversion<string>()
            .HasMaxLength(EnumMaxLength);
        incident.Property(value => value.ResolvedBy).HasColumnName("resolved_by");
        incident.Property(value => value.ResolvedAt).HasColumnName("resolved_at");
        incident.Property(value => value.ResolutionNote).HasColumnName("resolution_note").HasMaxLength(Incident.NoteMaxLength);
        incident.Property(value => value.BatchId).HasColumnName("batch_id");
        incident.Property(value => value.AdjustmentId).HasColumnName("adjustment_id");
        incident.Property(value => value.ParentIncidentId).HasColumnName("parent_incident_id");
        incident.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();

        incident.HasIndex(value => new { value.TenantId, value.Number }).IsUnique().HasDatabaseName(WmsDbContext.IncidentNumberIndex);
        // Dedupica el SyncConflict reintentado: sólo las novedades que nacen de una acción con clave.
        incident.HasIndex(value => new { value.TenantId, value.OperationKey }, WmsDbContext.IncidentOperationKeyIndex)
            .IsUnique()
            .HasFilter("operation_key IS NOT NULL");
        // La bandeja y el tablero: sólo lo que sigue abierto.
        incident.HasIndex(value => new { value.TenantId, value.WarehouseId, value.Status }, "IX_incidents_open")
            .HasFilter("status IN ('Open','InReview')");
        incident.HasIndex(value => value.VerificationTaskId, "IX_incidents_verification")
            .HasFilter("verification_task_id IS NOT NULL");
        // La bandeja (GET /incidents) ordena por reported_at y number descendentes dentro del tenant.
        incident.HasIndex(value => new { value.TenantId, value.ReportedAt, value.Number }, "IX_incidents_tenant_reported");
        incident.HasIndex(value => value.AdjustmentId, "IX_incidents_adjustment")
            .HasFilter("adjustment_id IS NOT NULL");
        // «¿La variante tiene novedades abiertas?» (DA-51: no se puede desactivar con una novedad abierta).
        incident.HasIndex(value => new { value.TenantId, value.VariantId }, "IX_incidents_variant_open")
            .HasFilter("status IN ('Open','InReview') AND variant_id IS NOT NULL");

        incident.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(value => value.WarehouseId)
            .HasConstraintName("FK_incidents_warehouse")
            .OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<Owner>()
            .WithMany()
            .HasForeignKey(value => value.OwnerId)
            .HasConstraintName("FK_incidents_owner")
            .OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<Location>()
            .WithMany()
            .HasForeignKey(value => value.LocationId)
            .HasConstraintName("FK_incidents_location")
            .OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<Lot>()
            .WithMany()
            .HasForeignKey(value => value.LotId)
            .HasConstraintName("FK_incidents_lot")
            .OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<Incident>()
            .WithMany()
            .HasForeignKey(value => value.ParentIncidentId)
            .HasConstraintName("FK_incidents_parent")
            .OnDelete(DeleteBehavior.Restrict);

        // La evidencia no conoce a su novedad: la clave es sombra y el dominio no la ve.
        incident.HasMany(value => value.Evidence)
            .WithOne()
            .HasForeignKey("IncidentId")
            .HasConstraintName("FK_incident_evidence_incident")
            .OnDelete(DeleteBehavior.Cascade);
        incident.Navigation(value => value.Evidence).UsePropertyAccessMode(PropertyAccessMode.Field);
        incident.HasMany(value => value.Events)
            .WithOne()
            .HasForeignKey(value => value.IncidentId)
            .HasConstraintName("FK_incident_events_incident")
            .OnDelete(DeleteBehavior.Cascade);
        incident.Navigation(value => value.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureEvidence(ModelBuilder modelBuilder)
    {
        var evidence = modelBuilder.Entity<IncidentEvidence>();
        evidence.ToTable("incident_evidence", Schema, table =>
            table.HasCheckConstraint("CK_incident_evidence_kind", "kind IN ('Photo','Scan')"));
        evidence.HasKey(value => value.Id);
        evidence.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        evidence.Property<Guid>("IncidentId").HasColumnName("incident_id");
        evidence.Property(value => value.FileId).HasColumnName("file_id");
        evidence.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(EnumMaxLength);
        evidence.Property(value => value.RawValue).HasColumnName("raw_value").HasMaxLength(IncidentEvidence.RawValueMaxLength);
        evidence.Property(value => value.Position).HasColumnName("position");
        evidence.HasIndex("IncidentId", nameof(IncidentEvidence.Position)).IsUnique().HasDatabaseName("IX_incident_evidence_position");
    }

    // Sólo se insertan: es la historia de la novedad. Sin CHECK en kind: el vocabulario crece sin migración.
    private static void ConfigureEvent(ModelBuilder modelBuilder)
    {
        var history = modelBuilder.Entity<IncidentEvent>();
        history.ToTable("incident_events", Schema);
        history.HasKey(value => value.Id);
        history.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        history.Property(value => value.TenantId).HasColumnName("tenant_id");
        history.Property(value => value.IncidentId).HasColumnName("incident_id");
        history.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(EnumMaxLength);
        history.Property(value => value.MemberId).HasColumnName("member_id");
        history.Property(value => value.Detail).HasColumnName("detail").HasMaxLength(Incident.NoteMaxLength);
        history.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        history.Property(value => value.RecordedAt).HasColumnName("recorded_at");
        history.HasIndex(value => new { value.IncidentId, value.RecordedAt }, "IX_incident_events_incident");
    }
}
