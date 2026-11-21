using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>coordinadora</c> (spec 2026-11-21 §5.1 y §7): la configuración por conexión y las guías. Proyecta <c>audit.entries</c> sin ser su
/// dueño (<c>ExcludeFromMigrations</c>), como Siigo. Sin FKs a otros esquemas: conexión, pedido y ciudad son referencias blandas.
/// </summary>
public sealed class CoordinadoraDbContext(DbContextOptions<CoordinadoraDbContext> options) : DbContext(options)
{
    public const string Schema = "coordinadora";

    /// <summary>Spec 2026-11-21 §5.5: una guía viva por pedido (<c>Pending</c>, <c>Created</c> o <c>Unknown</c>). El candado del pedido da
    /// la respuesta amable; este índice cierra la carrera. <c>CoordinadoraUnitOfWork</c> lo traduce por nombre a
    /// <c>409 coordinadora.shipment.already_exists</c>.</summary>
    public const string ShipmentsTenantOrderLiveIndex = "IX_shipments_tenant_order_live";

    internal DbSet<CoordinadoraSettings> Settings => Set<CoordinadoraSettings>();

    internal DbSet<CoordinadoraShipment> Shipments => Set<CoordinadoraShipment>();

    // Proyección de escritura (ExcludeFromMigrations): Audit es dueño de audit.entries.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureSettings(modelBuilder);
        ConfigureShipment(modelBuilder);
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<CoordinadoraSettings>();
        settings.ToTable("settings", Schema, table =>
        {
            table.HasCheckConstraint("CK_settings_packaging_type", "packaging_type IN (0, 1)");
            table.HasCheckConstraint("CK_settings_declared_value_minimum", "declared_value_minimum >= 0");
        });
        settings.HasKey(value => value.ConnectionId).HasName("PK_settings");
        settings.Property(value => value.ConnectionId).HasColumnName("connection_id").ValueGeneratedNever();
        settings.Property(value => value.TenantId).HasColumnName("tenant_id");
        settings.Property(value => value.SenderName).HasColumnName("sender_name").HasMaxLength(CoordinadoraSettings.NameMaxLength);
        settings.Property(value => value.SenderAddress).HasColumnName("sender_address").HasMaxLength(CoordinadoraSettings.AddressMaxLength);
        settings.Property(value => value.SenderPhone).HasColumnName("sender_phone").HasMaxLength(CoordinadoraSettings.PhoneMaxLength);
        settings.Property(value => value.SenderCityId).HasColumnName("sender_city_id");
        settings.Property(value => value.AccountCode).HasColumnName("account_code").HasMaxLength(CoordinadoraSettings.CodeMaxLength);
        settings.Property(value => value.ProductCode).HasColumnName("product_code").HasMaxLength(CoordinadoraSettings.CodeMaxLength);
        settings.Property(value => value.ServiceLevel).HasColumnName("service_level");
        settings.Property(value => value.Div).HasColumnName("div").HasMaxLength(CoordinadoraSettings.CodeMaxLength);
        settings.Property(value => value.DefaultHeightCm).HasColumnName("default_height_cm");
        settings.Property(value => value.DefaultWidthCm).HasColumnName("default_width_cm");
        settings.Property(value => value.DefaultLengthCm).HasColumnName("default_length_cm");
        settings.Property(value => value.DefaultWeightKg).HasColumnName("default_weight_kg").HasPrecision(8, 2);
        // §5.2: los defaults explícitos en la columna, para que la fila que nace con INSERT (connection_id, tenant_id) los tome.
        settings.Property(value => value.PackagingType).HasColumnName("packaging_type").HasDefaultValue(CoordinadoraSettings.PackagingAutomatic);
        settings.Property(value => value.DeclaredValueMinimum).HasColumnName("declared_value_minimum").HasPrecision(18, 2).HasDefaultValue(0m);
        settings.Property(value => value.CollectOnDelivery).HasColumnName("collect_on_delivery").HasDefaultValue(false);
        settings.Property(value => value.LabelFormat).HasColumnName("label_format").HasMaxLength(CoordinadoraSettings.LabelFormatMaxLength);
        settings.Property(value => value.Version).HasColumnName("version").HasDefaultValue(0L).IsConcurrencyToken();
        settings.Ignore(value => value.DefaultPackage);
        settings.HasIndex(value => value.TenantId).HasDatabaseName("IX_settings_tenant");
    }

    private static void ConfigureShipment(ModelBuilder modelBuilder)
    {
        var shipment = modelBuilder.Entity<CoordinadoraShipment>();
        shipment.ToTable("shipments", Schema);
        shipment.HasKey(value => value.Id).HasName("PK_shipments");
        shipment.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        shipment.Property(value => value.TenantId).HasColumnName("tenant_id");
        shipment.Property(value => value.ConnectionId).HasColumnName("connection_id");
        shipment.Property(value => value.OrderId).HasColumnName("order_id");
        shipment.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        // La petición entera, tal como salió. Texto y no jsonb: es lo que se mandó, byte a byte.
        shipment.Property(value => value.RequestJson).HasColumnName("request_json").HasColumnType("text");
        shipment.Property(value => value.GuideNumber).HasColumnName("guide_number").HasMaxLength(CoordinadoraShipment.GuideNumberMaxLength);
        shipment.Property(value => value.DeclaredValue).HasColumnName("declared_value").HasPrecision(18, 2);
        shipment.Property(value => value.LabelPdf).HasColumnName("label_pdf");
        shipment.Property(value => value.TrackingStatus).HasColumnName("tracking_status").HasMaxLength(CoordinadoraShipment.TrackingStatusMaxLength);
        shipment.Property(value => value.TrackingDetail).HasColumnName("tracking_detail").HasMaxLength(CoordinadoraShipment.TrackingDetailMaxLength);
        shipment.Property(value => value.TrackedAt).HasColumnName("tracked_at");
        shipment.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(CoordinadoraShipment.ErrorCodeMaxLength);
        shipment.Property(value => value.ErrorDetail).HasColumnName("error_detail").HasMaxLength(CoordinadoraShipment.ErrorDetailMaxLength);
        shipment.Property(value => value.Attempts).HasColumnName("attempts");
        shipment.Property(value => value.OrderVersion).HasColumnName("order_version");
        shipment.Property(value => value.SendingSince).HasColumnName("sending_since");
        shipment.Property(value => value.CreatedBy).HasColumnName("created_by");
        shipment.Property(value => value.CreatedAt).HasColumnName("created_at");
        shipment.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        shipment.Property(value => value.CancelledAt).HasColumnName("cancelled_at");
        shipment.Property(value => value.CancelledBy).HasColumnName("cancelled_by");
        shipment.Ignore(value => value.IsLive);
        shipment.Ignore(value => value.IsCreated);
        // Una viva por pedido. Parcial por el texto del estado: Rejected, Failed y Cancelled liberan el pedido. Los dos índices van sobre
        // las mismas columnas, así que se declaran con nombre: un segundo HasIndex sin nombre sobre las mismas propiedades es el mismo
        // índice y lo renombra, y la traducción por nombre de CoordinadoraUnitOfWork dejaría de casar.
        shipment.HasIndex(value => new { value.TenantId, value.OrderId }, ShipmentsTenantOrderLiveIndex)
            .IsUnique()
            .HasFilter("status IN ('Pending', 'Created', 'Unknown')");
        // La lectura de todas las filas del pedido (vivas y cerradas).
        shipment.HasIndex(value => new { value.TenantId, value.OrderId }, "IX_shipments_tenant_order");
    }
}
