using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>wms</c> (spec WMS 2026-10-31 §11): bodegas, ubicaciones, dueños, unidades de medida y perfiles de variante.
/// Proyecta <c>audit.entries</c> y <c>platform.outbox_messages</c> sin ser su dueño (ExcludeFromMigrations). Sin FKs a otros
/// esquemas: variantes, ciudades y proveedores son referencias blandas.
/// </summary>
public sealed class WmsDbContext(DbContextOptions<WmsDbContext> options) : DbContext(options)
{
    public const string Schema = "wms";

    // Nombres de índices y claves que WmsUnitOfWork traduce a un código de dominio. Se discrimina por nombre y no sólo por el
    // SqlState (precedente TenancyUnitOfWork): cada índice es una regla distinta y devolver el código equivocado manda a corregir
    // el campo equivocado.
    public const string WarehouseCodeIndex = "IX_warehouses_tenant_code";
    public const string LocationCodeIndex = "IX_locations_warehouse_code";
    public const string LocationVirtualRoleIndex = "IX_locations_warehouse_virtual_role";
    public const string LocationParentForeignKey = "FK_locations_parent";
    public const string OwnerSelfIndex = "IX_owners_tenant_self";
    public const string UomPrimaryKey = "PK_units_of_measure";
    public const string VariantProfilePrimaryKey = "PK_variant_profiles";
    public const string ConversionCodeIndex = "IX_uom_conversions_variant_code";
    public const string IdentifierValueIndex = "IX_identifiers_tenant_value";

    // Ledger y saldos (plan WMS-D). El upsert del ledger no pasa por SaveChanges, así que los dos CHECK de saldo los traduce el
    // ledger mismo a partir de estos nombres.
    public const string StockBalanceKeyIndex = "UQ_stock_balances_key";
    public const string StockBalanceNonNegativeCheck = "CK_stock_balances_on_hand_non_negative";
    public const string StockBalanceReservedCheck = "CK_stock_balances_reserved";
    public const string OperationReceiptPrimaryKey = "PK_operation_receipts";
    public const string LotCodeIndex = "IX_lots_owner_variant_code";
    public const string SerialUnitIndex = "IX_serial_units_variant_serial";
    public const string ReservationOpenIndex = "IX_reservations_open_key";
    public const string ReservationOpenDemandIndex = "IX_reservations_open_demand";
    public const string WarehousePrincipalIndex = "IX_warehouses_tenant_principal";
    public const string DocumentStockStatePrimaryKey = "PK_document_stock_states";
    public const string AdjustmentNumberIndex = "IX_adjustments_tenant_number";

    // Motor de tareas (spec 2026-11-26 §6.1).
    public const string TaskNumberIndex = "IX_tasks_tenant_number";
    public const string TaskMemberInProgressIndex = "IX_tasks_member_in_progress";

    // Novedades (spec 2026-11-26 §8.1).
    public const string IncidentNumberIndex = "IX_incidents_tenant_number";
    public const string IncidentOperationKeyIndex = "IX_incidents_operation_key";

    // FKs internas del ledger hacia ubicaciones y dueños (spec §11.2, creadas por InventoryLedgerSql.CreateInternalForeignKeys) y las de
    // las tareas, que declara WarehouseTaskModel. Borrar una ubicación o un dueño con historia da 23001 (RESTRICT): WmsUnitOfWork lo
    // traduce por nombre.
    public static readonly string[] LocationReferenceForeignKeys =
    [
        "FK_stock_movements_from_location", "FK_stock_movements_to_location", "FK_stock_balances_location", "FK_serial_units_location",
        "FK_reservations_location", "FK_adjustment_lines_location", "FK_task_lines_from_location", "FK_task_lines_to_location",
        "FK_incidents_location",
    ];

    public static readonly string[] OwnerReferenceForeignKeys =
    [
        "FK_stock_movements_owner", "FK_stock_balances_owner", "FK_lots_owner", "FK_serial_units_owner", "FK_reservations_owner",
        "FK_adjustments_owner", "FK_tasks_owner", "FK_incidents_owner",
    ];

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Owner> Owners => Set<Owner>();

    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    public DbSet<VariantLogisticsProfile> VariantProfiles => Set<VariantLogisticsProfile>();

    public DbSet<Identifier> Identifiers => Set<Identifier>();

    public DbSet<MemberScope> MemberScopes => Set<MemberScope>();

    public DbSet<DocumentStockState> DocumentStockStates => Set<DocumentStockState>();

    // Inventario básico: el inbox del consumidor de eventos de inventario (copia del de Purchases).
    internal DbSet<WmsInboxMessage> Inbox => Set<WmsInboxMessage>();

    internal DbSet<WmsOutboxMessage> Outbox => Set<WmsOutboxMessage>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureOutboxProjection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureWarehouse(modelBuilder);
        ConfigureLocation(modelBuilder);
        ConfigureOwner(modelBuilder);
        ConfigureUnitOfMeasure(modelBuilder);
        ConfigureVariantProfile(modelBuilder);
        ConfigureConversion(modelBuilder);
        ConfigureIdentifier(modelBuilder);
        ConfigureMemberScope(modelBuilder);
        ConfigureDocumentStockState(modelBuilder);
        ConfigureInbox(modelBuilder);
        InventoryLedgerModel.Configure(modelBuilder);
        WarehouseTaskModel.Configure(modelBuilder);
        IncidentModel.Configure(modelBuilder);
    }

    // Los enums van como texto y los `version` como bigint fijado a mano: un cambio futuro del tipo CLR no cambia el esquema. Los
    // CHECK repiten lo que el dominio ya valida, para que la base no dependa de quien escribe.
    private static void ConfigureWarehouse(ModelBuilder modelBuilder)
    {
        var warehouse = modelBuilder.Entity<Warehouse>();
        warehouse.ToTable("warehouses", Schema);
        warehouse.HasKey(value => value.Id);
        warehouse.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new WarehouseId(value))
            .ValueGeneratedNever();
        warehouse.Property(value => value.TenantId).HasColumnName("tenant_id");
        warehouse.Property(value => value.Code).HasColumnName("code").HasMaxLength(Warehouse.CodeMaxLength);
        warehouse.Property(value => value.Name).HasColumnName("name").HasMaxLength(Warehouse.NameMaxLength);
        // Referencia blanda a geography.cities: otro esquema, otro contexto.
        warehouse.Property(value => value.CityId).HasColumnName("city_id");
        warehouse.Property(value => value.IsActive).HasColumnName("is_active");
        warehouse.Property(value => value.IsPrincipal).HasColumnName("is_principal").HasDefaultValue(false);
        // Referencia blanda a locations (la FK circular con FK_locations_warehouse no aporta): EnsureGeneralLocation recrea la que falte.
        warehouse.Property(value => value.GeneralLocationId)
            .HasColumnName("general_location_id")
            .HasConversion(id => id!.Value.Value, value => new LocationId(value));
        // Igual que la general: referencias blandas por id (G-11); nulas porque no se rellenan las bodegas anteriores.
        warehouse.Property(value => value.ReceivingZoneId)
            .HasColumnName("receiving_zone_id")
            .HasConversion(id => id!.Value.Value, value => new LocationId(value));
        warehouse.Property(value => value.ReceivingDockId)
            .HasColumnName("receiving_dock_id")
            .HasConversion(id => id!.Value.Value, value => new LocationId(value));
        warehouse.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        warehouse.Property(value => value.CreatedAt).HasColumnName("created_at");
        warehouse.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        warehouse.HasIndex(value => new { value.TenantId, value.Code }).IsUnique().HasDatabaseName(WarehouseCodeIndex);
        // Una sola principal por tenant (inventario básico §5.3): la frena la base, no una consulta previa.
        warehouse.HasIndex(value => value.TenantId, WarehousePrincipalIndex).IsUnique().HasFilter("is_principal");
        warehouse.HasMany(value => value.Locations)
            .WithOne()
            .HasForeignKey(location => location.WarehouseId)
            .HasConstraintName("FK_locations_warehouse")
            .OnDelete(DeleteBehavior.Restrict);
        warehouse.Navigation(value => value.Locations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureLocation(ModelBuilder modelBuilder)
    {
        var location = modelBuilder.Entity<Location>();
        location.ToTable("locations", Schema, table =>
        {
            table.HasCheckConstraint("CK_locations_kind", "kind IN ('Zone','Aisle','Rack','Level','Bin','Dock','Virtual')");
            table.HasCheckConstraint(
                "CK_locations_role",
                "role IN ('Receiving','Storage','Picking','Shipping','Quarantine','Returns','Transit','Adjustment','Supplier','Customer')");
            table.HasCheckConstraint("CK_locations_status", "status IN ('Active','Blocked','Counting')");
            // Una virtual nunca guarda existencias: lo dice el dominio y lo repite la base.
            table.HasCheckConstraint("CK_locations_virtual_storage", "NOT is_virtual OR NOT allows_storage");
        });
        location.HasKey(value => value.Id);
        location.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new LocationId(value))
            .ValueGeneratedNever();
        location.Property(value => value.TenantId).HasColumnName("tenant_id");
        location.Property(value => value.WarehouseId)
            .HasColumnName("warehouse_id")
            .HasConversion(id => id.Value, value => new WarehouseId(value));
        location.Property(value => value.ParentId)
            .HasColumnName("parent_id")
            .HasConversion(id => id!.Value.Value, value => new LocationId(value));
        location.Property(value => value.Code).HasColumnName("code").HasMaxLength(32);
        location.Property(value => value.Path).HasColumnName("path").HasMaxLength(256);
        location.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        location.Property(value => value.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(16);
        location.Property(value => value.IsVirtual).HasColumnName("is_virtual");
        location.Property(value => value.AllowsStorage).HasColumnName("allows_storage");
        location.Property(value => value.AllowsPicking).HasColumnName("allows_picking");
        location.ComplexProperty(value => value.Capacity, capacity =>
        {
            capacity.Property(value => value.MaxWeightKg).HasColumnName("max_weight_kg").HasPrecision(12, 3);
            capacity.Property(value => value.MaxVolumeM3).HasColumnName("max_volume_m3").HasPrecision(12, 6);
            capacity.Property(value => value.MaxUnits).HasColumnName("max_units");
        });
        location.Property(value => value.PutawayPriority).HasColumnName("putaway_priority");
        // Clases de almacenamiento: un puñado de textos sin identidad que se reemplaza entero con la ubicación (mismo criterio que
        // los permisos de un rol). `StorageClasses` es la vista de sólo lectura de ese campo.
        location.PrimitiveCollection<List<string>>("_storageClasses")
            .HasColumnName("storage_classes")
            .HasColumnType("text[]")
            .IsRequired()
            .HasDefaultValueSql("'{}'");
        location.Ignore(value => value.StorageClasses);
        location.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        location.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        location.Property(value => value.CreatedAt).HasColumnName("created_at");
        location.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        location.HasOne<Location>()
            .WithMany()
            .HasForeignKey(value => value.ParentId)
            .HasConstraintName(LocationParentForeignKey)
            .OnDelete(DeleteBehavior.Restrict);
        location.HasIndex(value => new { value.WarehouseId, value.Code }).IsUnique().HasDatabaseName(LocationCodeIndex);
        // Una sola virtual por rol y bodega: las cuatro nacen con la bodega y no se duplican.
        // Con nombre en el HasIndex y no sólo HasDatabaseName: dos índices sobre las mismas propiedades se fusionan en uno si no se
        // distinguen por nombre, y el último pisaría al único parcial.
        location.HasIndex(value => new { value.WarehouseId, value.Role }, LocationVirtualRoleIndex)
            .IsUnique()
            .HasFilter("is_virtual");
        location.HasIndex(value => new { value.WarehouseId, value.Role }, "IX_locations_warehouse_role");
        // text_pattern_ops: el subárbol se busca con `path LIKE 'prefijo%'`, que un btree común no sirve fuera de la colación C.
        location.HasIndex(value => value.Path)
            .HasDatabaseName("IX_locations_path")
            .HasMethod("btree")
            .HasOperators("text_pattern_ops");
    }

    private static void ConfigureOwner(ModelBuilder modelBuilder)
    {
        var owner = modelBuilder.Entity<Owner>();
        owner.ToTable("owners", Schema, table =>
            table.HasCheckConstraint("CK_owners_kind", "kind IN ('Self','Client')"));
        owner.HasKey(value => value.Id);
        owner.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new OwnerId(value))
            .ValueGeneratedNever();
        owner.Property(value => value.TenantId).HasColumnName("tenant_id");
        owner.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        owner.Property(value => value.Name).HasColumnName("name").HasMaxLength(Owner.NameMaxLength);
        owner.Property(value => value.Identification).HasColumnName("identification").HasMaxLength(Owner.IdentificationMaxLength);
        owner.Property(value => value.ContactEmail).HasColumnName("contact_email").HasMaxLength(254);
        owner.Property(value => value.IsActive).HasColumnName("is_active");
        owner.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        owner.Property(value => value.CreatedAt).HasColumnName("created_at");
        owner.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // Con nombre en el HasIndex: dos índices sobre la misma propiedad se fusionan si sólo se distinguen por HasDatabaseName.
        owner.HasIndex(value => value.TenantId, "IX_owners_tenant");
        // Un solo Self por tenant (S5): lo frena la base, no una consulta previa.
        owner.HasIndex(value => value.TenantId, OwnerSelfIndex).IsUnique().HasFilter("kind = 'Self'");
    }

    private static void ConfigureUnitOfMeasure(ModelBuilder modelBuilder)
    {
        var uom = modelBuilder.Entity<UnitOfMeasure>();
        uom.ToTable("units_of_measure", Schema, table =>
            table.HasCheckConstraint("CK_units_of_measure_precision", "precision BETWEEN 0 AND 6"));
        uom.HasKey(value => new { value.TenantId, value.Code }).HasName(UomPrimaryKey);
        uom.Property(value => value.TenantId).HasColumnName("tenant_id");
        uom.Property(value => value.Code)
            .HasColumnName("code")
            .HasConversion(code => code.Value, value => UomCode.Parse(value))
            .HasMaxLength(UomCode.MaxLength);
        uom.Property(value => value.Name).HasColumnName("name").HasMaxLength(UnitOfMeasure.NameMaxLength);
        uom.Property(value => value.Precision).HasColumnName("precision");
        uom.Property(value => value.IsSystem).HasColumnName("is_system");
        uom.Property(value => value.CreatedAt).HasColumnName("created_at");
    }

    private static void ConfigureVariantProfile(ModelBuilder modelBuilder)
    {
        var profile = modelBuilder.Entity<VariantLogisticsProfile>();
        profile.ToTable("variant_profiles", Schema, table =>
        {
            table.HasCheckConstraint("CK_variant_profiles_tracking", "tracking IN ('None','Lot','Serial')");
            table.HasCheckConstraint("CK_variant_profiles_precision", "base_uom_precision BETWEEN 0 AND 6");
        });
        profile.HasKey(value => new { value.TenantId, value.VariantId }).HasName(VariantProfilePrimaryKey);
        profile.Property(value => value.TenantId).HasColumnName("tenant_id");
        // Variante y producto son referencias blandas a catalog: otro esquema, otro contexto.
        profile.Property(value => value.VariantId).HasColumnName("variant_id");
        profile.Property(value => value.ProductId).HasColumnName("product_id");
        profile.Property(value => value.BaseUom)
            .HasColumnName("base_uom")
            .HasConversion(code => code.Value, value => UomCode.Parse(value))
            .HasMaxLength(UomCode.MaxLength);
        profile.Property(value => value.BaseUomPrecision).HasColumnName("base_uom_precision");
        profile.Property(value => value.Tracking).HasColumnName("tracking").HasConversion<string>().HasMaxLength(16);
        profile.Property(value => value.TracksExpiry).HasColumnName("tracks_expiry");
        profile.Property(value => value.WeightKg).HasColumnName("weight_kg").HasPrecision(12, 3);
        profile.Property(value => value.VolumeM3).HasColumnName("volume_m3").HasPrecision(12, 6);
        profile.Property(value => value.LengthCm).HasColumnName("length_cm").HasPrecision(10, 2);
        profile.Property(value => value.WidthCm).HasColumnName("width_cm").HasPrecision(10, 2);
        profile.Property(value => value.HeightCm).HasColumnName("height_cm").HasPrecision(10, 2);
        profile.Property(value => value.StorageClass).HasColumnName("storage_class").HasMaxLength(32);
        profile.Property(value => value.DisplayUom)
            .HasColumnName("display_uom")
            .HasConversion(code => code!.Value.Value, value => UomCode.Parse(value))
            .HasMaxLength(UomCode.MaxLength);
        profile.Property(value => value.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        profile.Property(value => value.CreatedAt).HasColumnName("created_at");
        profile.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        profile.HasMany(value => value.Conversions)
            .WithOne()
            .HasForeignKey("TenantId", "VariantId")
            .HasConstraintName("FK_uom_conversions_profile")
            // ClientCascade y no Restrict: con Restrict EF rechaza sacar una conversión de la colección del perfil ("association
            // severed"). La FK de la base sigue siendo RESTRICT: se fija a mano en InitialWms porque el generador no emite nada
            // para ClientCascade. Así quitar una conversión es un DELETE directo del hijo y borrar el perfil no arrastra nada.
            .OnDelete(DeleteBehavior.ClientCascade);
        profile.Navigation(value => value.Conversions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureConversion(ModelBuilder modelBuilder)
    {
        var conversion = modelBuilder.Entity<UomConversion>();
        conversion.ToTable("uom_conversions", Schema, table =>
            table.HasCheckConstraint("CK_uom_conversions_factor", "factor > 0"));
        conversion.HasKey(value => value.Id);
        conversion.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        // La conversión no conoce a su perfil: la clave compuesta es sombra y el dominio no la ve.
        conversion.Property<Guid>("TenantId").HasColumnName("tenant_id");
        conversion.Property<Guid>("VariantId").HasColumnName("variant_id");
        conversion.Property(value => value.Code).HasColumnName("code").HasMaxLength(UomConversion.CodeMaxLength);
        conversion.Property(value => value.Factor).HasColumnName("factor").HasPrecision(18, 6);
        conversion.Property(value => value.Barcode).HasColumnName("barcode").HasMaxLength(64);
        // El dominio no guarda cuándo nació la conversión: lo pone la base.
        conversion.Property<DateTimeOffset>("CreatedAt").HasColumnName("created_at").HasDefaultValueSql("now()");
        conversion.HasIndex("TenantId", "VariantId", nameof(UomConversion.Code)).IsUnique().HasDatabaseName(ConversionCodeIndex);
    }

    private static void ConfigureIdentifier(ModelBuilder modelBuilder)
    {
        var identifier = modelBuilder.Entity<Identifier>();
        identifier.ToTable("identifiers", Schema, table =>
        {
            table.HasCheckConstraint("CK_identifiers_kind", "kind IN ('Variant','Presentation','Lot','LogisticUnit')");
            table.HasCheckConstraint(
                "CK_identifiers_symbology",
                "symbology IN ('Ean13','Ean8','Upc','Code128','Qr','Gtin','Sscc','Internal')");
        });
        identifier.HasKey(value => value.Id);
        identifier.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        identifier.Property(value => value.TenantId).HasColumnName("tenant_id");
        identifier.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        identifier.Property(value => value.Symbology).HasColumnName("symbology").HasConversion<string>().HasMaxLength(16);
        identifier.Property(value => value.Value).HasColumnName("value").HasMaxLength(Identifier.ValueMaxLength);
        // Apunta a una variante, una presentación, un lote o una unidad logística: tablas distintas, por eso sin FK.
        identifier.Property(value => value.TargetId).HasColumnName("target_id");
        identifier.Property(value => value.ConversionId).HasColumnName("conversion_id");
        identifier.Property(value => value.IsPrimary).HasColumnName("is_primary");
        identifier.Property(value => value.CreatedAt).HasColumnName("created_at");
        identifier.HasIndex(value => new { value.TenantId, value.Value }).IsUnique().HasDatabaseName(IdentifierValueIndex);
        identifier.HasIndex(value => new { value.TenantId, value.TargetId }).HasDatabaseName("IX_identifiers_tenant_target");
    }

    // Inventario básico (§5.2): una fila por documento. Los nombres de columna van a mano: el consumidor los escribe también en SQL.
    private static void ConfigureDocumentStockState(ModelBuilder modelBuilder)
    {
        var state = modelBuilder.Entity<DocumentStockState>();
        state.ToTable("document_stock_states", Schema, table =>
        {
            table.HasCheckConstraint("CK_document_stock_states_type", "document_type IN ('PurchaseOrder','Order','PosSale')");
            table.HasCheckConstraint(
                "CK_document_stock_states_status",
                "status IN ('None','Reserved','TakenOutByPrepare','TakenOutByArchive','Released','Parked','HandedToOutbound','Received','ReceivedByWms','Shipped','Voided')");
        });
        state.HasKey(value => new { value.TenantId, value.DocumentType, value.DocumentId }).HasName(DocumentStockStatePrimaryKey);
        state.Property(value => value.TenantId).HasColumnName("tenant_id");
        state.Property(value => value.DocumentType).HasColumnName("document_type").HasMaxLength(32);
        state.Property(value => value.DocumentId).HasColumnName("document_id");
        state.Property(value => value.LastVersion).HasColumnName("last_version").HasColumnType("bigint");
        state.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24);
        state.Property(value => value.WarehouseId).HasColumnName("warehouse_id");
        state.Property(value => value.LocationId).HasColumnName("location_id");
        state.Property(value => value.PostingBatchId).HasColumnName("posting_batch_id");
        state.Property(value => value.PostingOccurredAt).HasColumnName("posting_occurred_at");
        // Las líneas apartadas: un puñado de ids sin identidad que se reemplaza entero con la fila.
        state.PrimitiveCollection<List<Guid>>("_skippedLineIds")
            .HasColumnName("skipped_line_ids")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasDefaultValueSql("'[]'::jsonb");
        state.Ignore(value => value.SkippedLineIds);
        state.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // El panel del WMS y T3 buscan el documento de una orden por su estado.
        state.HasIndex(value => new { value.TenantId, value.Status }, "IX_document_stock_states_tenant_status");
    }

    // Copia del inbox de Purchases: una fila con processed_at nulo es un mensaje reclamado y sin terminar. Los nombres van a mano
    // porque el reclamo los escribe en SQL crudo.
    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<WmsInboxMessage>();
        inbox.ToTable("inbox_messages", Schema);
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    // Nace vacía y nadie la lee en este corte (S18, D-07); sin FK a owners ni warehouses: la fase 3 decide si las lleva.
    private static void ConfigureMemberScope(ModelBuilder modelBuilder)
    {
        var scope = modelBuilder.Entity<MemberScope>();
        scope.ToTable("member_scopes", Schema, table =>
            table.HasCheckConstraint("CK_member_scopes_target", "owner_id IS NOT NULL OR warehouse_id IS NOT NULL"));
        scope.HasKey(value => value.Id);
        scope.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        scope.Property(value => value.TenantId).HasColumnName("tenant_id");
        scope.Property(value => value.MemberId).HasColumnName("member_id");
        scope.Property(value => value.OwnerId).HasColumnName("owner_id");
        scope.Property(value => value.WarehouseId).HasColumnName("warehouse_id");
        scope.Property(value => value.CreatedAt).HasColumnName("created_at");
        scope.HasIndex(value => new { value.TenantId, value.MemberId }).HasDatabaseName("IX_member_scopes_tenant_member");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<WmsOutboxMessage>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        // correlation_id y attempts son NOT NULL sin default: una proyección que no los mapea no puede insertar.
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
        outbox.Property(value => value.LastError).HasColumnName("last_error");
    }
}

/// <summary>Proyección del outbox de plataforma: el módulo publica sus eventos en la misma transacción que el cambio.</summary>
internal sealed class WmsOutboxMessage
{
    public Guid Id { get; init; }

    public string EventName { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public int Attempts { get; init; }

    public string? LastError { get; init; }
}
