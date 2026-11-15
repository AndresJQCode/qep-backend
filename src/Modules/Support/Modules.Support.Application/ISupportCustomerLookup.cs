namespace Modules.Support.Application;

public sealed record SupportCustomerRef(Guid Id, string Name, bool IsActive);

/// <summary>Clientes vistos desde Soporte (copia recortada de <c>ILoansCustomerLookup</c>); el adaptador vive en Bootstrapper, sobre
/// <c>CustomersDbContext</c>. Un cliente fusionado está inactivo.</summary>
public interface ISupportCustomerLookup
{
    Task<SupportCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SupportCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Los ids de los clientes del tenant cuyo nombre contiene <paramref name="term"/>: la búsqueda de la bandeja.</summary>
    Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken);
}
