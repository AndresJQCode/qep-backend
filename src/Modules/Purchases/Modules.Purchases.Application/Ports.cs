using Modules.Purchases.Domain;

namespace Modules.Purchases.Application;

/// <summary>
/// Las órdenes del módulo. Toda lectura va acotada al tenant; <see cref="Add"/> y <see cref="Remove"/> no lo reciben porque el
/// agregado ya lo lleva.
/// </summary>
public interface IPurchaseOrderRepository
{
    /// <summary>La orden con sus líneas y su historial, o <c>null</c> si no es del tenant.</summary>
    Task<PurchaseOrder?> GetAsync(Guid tenantId, PurchaseOrderId id, CancellationToken cancellationToken);

    void Add(PurchaseOrder order);

    void Remove(PurchaseOrder order);

    /// <summary>
    /// Una página ordenada por <c>created_at DESC, id DESC</c> (D-13), con las líneas cargadas. <paramref name="numberSearch"/>
    /// y <paramref name="supplierIdsMatchingSearch"/> van en OR (D-11).
    /// </summary>
    Task<(IReadOnlyList<PurchaseOrder> Items, int Total)> SearchAsync(
        Guid tenantId,
        PurchaseOrderStatus? status,
        Guid? supplierId,
        string? numberSearch,
        IReadOnlyCollection<Guid>? supplierIdsMatchingSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cuántas órdenes hay por estado con los mismos filtros de <see cref="SearchAsync"/> salvo el estado, en una sola consulta.
    /// Un estado sin filas no viene: el handler lo completa en 0 (D-18).
    /// </summary>
    Task<IReadOnlyDictionary<PurchaseOrderStatus, int>> CountByStatusAsync(
        Guid tenantId,
        Guid? supplierId,
        string? numberSearch,
        IReadOnlyCollection<Guid>? supplierIdsMatchingSearch,
        CancellationToken cancellationToken);
}

/// <summary>
/// El siguiente consecutivo del tenant (<c>OC-0001</c>). Exige una transacción abierta con
/// <see cref="IPurchasesUnitOfWork.BeginTransactionAsync"/>: el candado de la fila serializa dos altas y un alta que falla
/// deshace su número.
/// </summary>
public interface IPurchaseOrderNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Copia de <c>ISupportUnitOfWork</c>: guardar y, para el alta, una transacción explícita con el número.</summary>
public interface IPurchasesUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Abre una transacción sobre la conexión del contexto del módulo. Sin confirmarla, se revierte al liberarla y el
    /// número no se pierde.</summary>
    Task<IPurchasesTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

public interface IPurchasesTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Auditoría como puerto propio (mismo contrato que <c>ILoansAuditPublisher</c>): el adaptador acumula la entrada en el contexto
/// del módulo para que se confirme o se revierta con el cambio de negocio (D-16).
/// </summary>
public interface IPurchasesAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

/// <summary>
/// Un proveedor del directorio visto desde Compras. <c>IdentificationType</c> va con el nombre del cable. Mayorista fase 3:
/// <c>LinkedTenantId</c> es el tenant mayorista con que el proveedor está vinculado (null si es externo); una orden lo fija al
/// guardarse (D-10).
/// </summary>
public sealed record PurchasesSupplierRef(
    Guid Id,
    string Name,
    string IdentificationType,
    string IdentificationNumber,
    bool IsActive,
    Guid? LinkedTenantId = null,
    string? Email = null);

/// <summary>El directorio de proveedores; adaptador en Bootstrapper sobre Suppliers. Todo acotado al tenant.</summary>
public interface IPurchasesSupplierLookup
{
    Task<PurchasesSupplierRef?> FindAsync(Guid tenantId, Guid supplierId, CancellationToken cancellationToken);

    /// <summary>En lote: los ids que no son del tenant no vienen.</summary>
    Task<IReadOnlyDictionary<Guid, PurchasesSupplierRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken);

    /// <summary>Ids de los proveedores cuyo nombre o razón social contiene <paramref name="search"/>, con tope (D-11).</summary>
    Task<IReadOnlyList<Guid>> SearchIdsAsync(Guid tenantId, string search, int take, CancellationToken cancellationToken);
}

/// <summary>Una variante del catálogo con lo que la línea copia: código, nombre visible y tasa de IVA actual (0 sin tasa).</summary>
public sealed record PurchasesVariantRef(Guid VariantId, string Code, string Name, decimal TaxRatePercent);

/// <summary>El catálogo; adaptador en Bootstrapper sobre Catalog. No filtra por activo (D-6).</summary>
public interface IPurchasesCatalogLookup
{
    Task<IReadOnlyDictionary<Guid, PurchasesVariantRef>> FindVariantsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <summary>Nombres visibles de membresías del tenant, en cualquier estado; adaptador en Bootstrapper sobre Tenancy.</summary>
public interface IPurchasesMemberDirectory
{
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
