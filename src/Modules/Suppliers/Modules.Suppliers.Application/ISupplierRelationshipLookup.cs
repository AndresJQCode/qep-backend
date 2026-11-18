namespace Modules.Suppliers.Application;

/// <summary>
/// Mayorista fase 3 (adenda del contrato): el estado de la relación entre este tenant (el distribuidor) y cada mayorista
/// vinculado, por nombre (<c>Active</c>, <c>Blocked</c>, <c>Invited</c>). Adaptador en Bootstrapper sobre Wholesale; un mayorista
/// sin relación con este tenant no viene. En lote: la lista pide una vez por página.
/// </summary>
public interface ISupplierRelationshipLookup
{
    Task<IReadOnlyDictionary<Guid, string>> FindStatusesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> wholesalerTenantIds, CancellationToken cancellationToken);
}
