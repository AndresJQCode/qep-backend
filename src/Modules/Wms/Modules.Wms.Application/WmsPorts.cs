using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>Una sola unidad de trabajo por módulo: los repositorios sólo acumulan cambios y el handler guarda una vez, así el
/// cambio, su auditoría y su outbox commitean o se revierten juntos.</summary>
public interface IWmsUnitOfWork
{
    /// <summary>La transacción explícita de los flujos que escriben al ledger: disponerla sin guardar la deshace.</summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Manda los cambios acumulados a la transacción abierta, sin confirmarla: para lo que un SQL posterior necesita ver en la
    /// base. Traduce los errores igual que <see cref="SaveChangesAsync"/>.</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Sólo para documentos (ajuste, movimiento a reversar, preview): <c>false</c> si otra transacción lo tiene. No espera, y
    /// lo suelta el fin de la transacción. Exige la de <see cref="BeginTransactionAsync"/>.</summary>
    Task<bool> TryLockDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Lock de transacción <b>bloqueante</b> sobre una clave de negocio (no un documento): quien llega segundo espera a que el
    /// primero confirme en vez de recibir un 409. Exige la de <see cref="BeginTransactionAsync"/> y lo suelta el fin de ella.</summary>
    Task LockAsync(string key, CancellationToken cancellationToken);

    /// <summary>Guarda. Con una transacción abierta, además la confirma.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public enum WmsActorType
{
    Human,
    System,
    ApiKey,
}

/// <summary>
/// Auditoría atómica (RF-102): la entrada se acumula en el <c>WmsDbContext</c> y commitea con el cambio. <c>changedFields</c> viaja
/// como objeto JSON (S23): el recorder de otros módulos guarda <c>[]</c>, pero acá el reporte de auditoría necesita saber qué campo
/// cambió.
/// </summary>
public interface IWmsAuditRecorder
{
    void Record(
        Guid tenantId,
        Guid actorId,
        WmsActorType actorType,
        string action,
        string resourceType,
        string resourceId,
        IReadOnlyDictionary<string, string?> changedFields,
        DateTimeOffset occurredAt);
}

public static class WmsAuditActions
{
    public static class Resources
    {
        public const string Warehouse = "wms.warehouse";
        public const string Location = "wms.location";
        public const string Owner = "wms.owner";
        public const string Profile = "wms.variant_profile";
        public const string Uom = "wms.uom";
        public const string Adjustment = "wms.adjustment";
        public const string Movement = "wms.stock_movement";
        public const string Import = "wms.import";
        public const string Export = "wms.export";
        public const string Task = "wms.task";
    }

    public const string WarehouseCreated = "wms.warehouse.created";
    public const string WarehouseUpdated = "wms.warehouse.updated";
    public const string LocationCreated = "wms.location.created";
    public const string LocationUpdated = "wms.location.updated";
    public const string LocationDeleted = "wms.location.deleted";
    public const string OwnerCreated = "wms.owner.created";
    public const string OwnerUpdated = "wms.owner.updated";
    public const string ProfileCreated = "wms.profile.created";
    public const string ProfileUpdated = "wms.profile.updated";
    public const string UomCreated = "wms.uom.created";
    public const string AdjustmentProposed = "wms.adjustment.proposed";
    public const string AdjustmentApproved = "wms.adjustment.approved";
    public const string AdjustmentPosted = "wms.adjustment.posted";
    public const string AdjustmentRejected = "wms.adjustment.rejected";
    public const string StockImported = "wms.stock.imported";
    public const string MovementReversed = "wms.movement.reversed";
    public const string StockExported = "wms.stock.exported";

    // Sólo las acciones del supervisor se auditan (spec 2026-11-26 §6.12): las del operario quedan en la historia de la tarea y en
    // el movimiento del ledger.
    public const string TaskCreated = "wms.task.created";
    public const string TaskPublished = "wms.task.published";
    public const string TaskHeld = "wms.task.held";
    public const string TaskAssigned = "wms.task.assigned";
    public const string TaskUnassigned = "wms.task.unassigned";
    public const string TaskPriorityChanged = "wms.task.priority_changed";
    public const string TaskCancelled = "wms.task.cancelled";
    public const string TaskLineCancelled = "wms.task.line_cancelled";
}

public interface IWarehouseRepository
{
    void Add(Warehouse warehouse);

    /// <summary>Con sus ubicaciones y rastreada: el agregado necesita el árbol entero para sus reglas.</summary>
    Task<Warehouse?> GetAsync(Guid tenantId, WarehouseId id, CancellationToken ct);

    /// <summary>Con sus ubicaciones, sin rastreo.</summary>
    Task<IReadOnlyList<Warehouse>> ListAsync(Guid tenantId, bool includeInactive, CancellationToken ct);

    Task<Location?> GetLocationAsync(Guid tenantId, LocationId id, CancellationToken ct);

    Task<Location?> GetVirtualLocationAsync(Guid tenantId, WarehouseId warehouseId, LocationRole role, CancellationToken ct);

    void RemoveLocation(Location location);

    /// <summary>Por código dentro de la bodega (S7: nunca por <c>identifiers</c>). El código se normaliza con
    /// <see cref="LocationCodes.Normalize"/> y se compara ordinal; uno que ni siquiera tiene forma de código no existe, no es un error.
    /// Sin rastreo.</summary>
    Task<Location?> FindLocationByCodeAsync(Guid tenantId, WarehouseId warehouseId, string code, CancellationToken ct);
}

public interface IOwnerRepository
{
    void Add(Owner owner);

    Task<Owner?> FindAsync(Guid tenantId, OwnerId id, CancellationToken ct);

    /// <summary>Lanza <see cref="InvalidOperationException"/> si falta: sólo <see cref="IWmsProvisioner"/> escribe el Self (S5), así
    /// que quien llama tiene que haber provisionado antes.</summary>
    Task<Owner> GetSelfAsync(Guid tenantId, CancellationToken ct);

    /// <summary>El Self primero, luego por nombre.</summary>
    Task<IReadOnlyList<Owner>> ListAsync(Guid tenantId, CancellationToken ct);
}

public interface IUnitOfMeasureRepository
{
    void Add(UnitOfMeasure uom);

    Task<UnitOfMeasure?> FindAsync(Guid tenantId, UomCode code, CancellationToken ct);

    /// <summary>Las de sistema primero, luego por código.</summary>
    Task<IReadOnlyList<UnitOfMeasure>> ListAsync(Guid tenantId, CancellationToken ct);
}

public interface IVariantProfileRepository
{
    void Add(VariantLogisticsProfile profile);

    Task<VariantLogisticsProfile?> FindAsync(Guid tenantId, Guid variantId, CancellationToken ct);

    Task<IReadOnlyList<VariantLogisticsProfile>> ListAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct);

    /// <summary>P-8: el perfil por defecto nace al primer acceso. Crea con <c>CreateDefault</c> más los identificadores de la variante
    /// y audita <c>ProfileCreated</c> como System; lanza <c>wms.profile.variant_unknown</c> si Catalog no conoce la variante. El
    /// llamador guarda.</summary>
    Task<VariantLogisticsProfile> GetOrCreateDefaultAsync(Guid tenantId, Guid variantId, CancellationToken ct);
}

public interface IIdentifierRepository
{
    void AddRange(IEnumerable<Identifier> identifiers);

    Task<IReadOnlyList<Identifier>> ListByTargetAsync(Guid tenantId, Guid targetId, CancellationToken ct);

    void RemoveRange(IEnumerable<Identifier> identifiers);
}

/// <summary>
/// El único escritor del Self y de las seis unidades de sistema (S5): <c>INSERT … ON CONFLICT DO NOTHING</c> en una transacción
/// propia. Tenancy no avisa al prender un módulo, así que se provisiona en el primer acceso; los handlers de dueños, unidades y
/// perfiles lo llaman tras autorizar, listados incluidos (única excepción a «un GET no escribe», P-8).
/// </summary>
public interface IWmsProvisioner
{
    Task EnsureAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Existencia de la variante en el libro de inventario: saldo físico (no virtual) distinto de cero.</summary>
public interface IVariantStockProbe
{
    Task<bool> HasStockAsync(Guid tenantId, Guid variantId, CancellationToken ct);

    /// <summary>Las variantes de <paramref name="variantIds"/> que tienen existencias, en una sola consulta (listado de perfiles).</summary>
    Task<IReadOnlySet<Guid>> HasStockForManyAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct);
}

public interface ILocationStockProbe
{
    Task<bool> HasStockAsync(Guid tenantId, Guid locationId, CancellationToken ct);
}

public interface IOwnerStockProbe
{
    Task<bool> HasStockAsync(Guid tenantId, Guid ownerId, CancellationToken ct);
}

/// <summary>Lo que Bodegas copia de una variante de Catalog. Ids desconocidos o de otro tenant no aparecen.</summary>
public sealed record WmsVariantSnapshot(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string VariantName,
    string Code,
    string? Barcode,
    IReadOnlyList<int> PackagingUnits,
    bool IsActive);

public interface IWmsCatalogReader
{
    Task<IReadOnlyList<WmsVariantSnapshot>> GetVariantsAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct);

    /// <summary>Todas las variantes cuyo código, sin distinguir mayúsculas, está entre <paramref name="codes"/>. La igualdad exacta de
    /// Catalog no sirve para detectar que <c>abc</c> y <c>ABC</c> son dos variantes distintas con el mismo código escrito.</summary>
    Task<IReadOnlyList<WmsVariantSnapshot>> FindVariantsByCodesAsync(Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct);
}

public sealed record WmsCityRef(Guid CityId, string Name);

public interface IWmsGeographyReader
{
    Task<IReadOnlyDictionary<Guid, WmsCityRef>> FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken ct);
}

/// <summary>Lo que una etiqueta imprime de una ubicación: el código que se codifica en Code 128, la ruta legible y la bodega que
/// da el nombre del archivo.</summary>
public sealed record LocationLabel(string Code, string Path, string WarehouseCode);

/// <summary>RF-004: las etiquetas de ubicación en un solo PDF. Application no sabe cómo se arma; Infrastructure lo hace con una
/// plantilla Typst.</summary>
public interface IWmsLabelRenderer
{
    Task<byte[]> RenderLocationLabelsAsync(IReadOnlyList<LocationLabel> labels, CancellationToken ct);
}

/// <summary>La salida hacia <c>qcode-pdf</c>. Vive aparte del renderer porque la URL y la clave son de <c>Quotations:Pdf</c> y el
/// módulo no puede ver Quotations (WmsLayerTests): el adaptador está en Bootstrapper. Un fallo se lanza como
/// <c>wms.label.render_failed</c>.</summary>
public interface IWmsPdfGateway
{
    Task<byte[]> RenderAsync(string source, object data, string fileName, CancellationToken ct);
}
