namespace Modules.Billing.Application;

public sealed record BillingCustomerRef(Guid Id, string Name, string? PhoneE164, string? Email, bool IsActive);

/// <summary>Clientes vistos desde Billing (spec §3.3); el adaptador vive en Bootstrapper, sobre <c>CustomersDbContext</c>, como <c>QuotationCustomerLookup</c>.</summary>
public interface IBillingCustomerLookup
{
    Task<BillingCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BillingCustomerRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Los ids de los clientes del tenant cuyo nombre coincide con <paramref name="term"/>: el filtro <c>search</c> de las listas.</summary>
    Task<IReadOnlyCollection<Guid>> SearchIdsAsync(Guid tenantId, string term, CancellationToken cancellationToken);
}
