namespace Modules.Loans.Application;

public sealed record LoansCustomerRef(Guid Id, string Name, string? PhoneE164, string? Email, bool IsActive);

/// <summary>Clientes vistos desde Loans; el adaptador vive en Bootstrapper, sobre <c>CustomersDbContext</c>, como <c>ServiceContractsCustomerLookup</c>.</summary>
public interface ILoansCustomerLookup
{
    Task<LoansCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LoansCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Los ids de los clientes del tenant cuyo nombre coincide con <paramref name="term"/>: el filtro <c>search</c> de las listas.</summary>
    Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken);
}