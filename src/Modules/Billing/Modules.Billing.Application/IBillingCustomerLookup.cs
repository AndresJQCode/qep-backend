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

    // El emparejamiento de la importación (§3.2b): listas y no uno solo, porque más de un resultado es customer_ambiguous. Sin
    // los fusionados: apuntan al que quedó, que es el que hay que emparejar.

    /// <summary>Por documento: <paramref name="identificationType"/> es un valor de <see cref="IBillingIdentificationTypes"/>.</summary>
    Task<IReadOnlyList<BillingCustomerRef>> FindByIdentificationAsync(
        Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken);

    Task<IReadOnlyList<BillingCustomerRef>> FindByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken);

    /// <summary>Por correo, sin mayúsculas.</summary>
    Task<IReadOnlyList<BillingCustomerRef>> FindByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken);
}
