using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de <c>Customers</c> al puerto que <c>quotations</c> declara.
///
/// Vive acá y no en ninguno de los dos módulos, mismo criterio que <c>ProductImageLookup</c>
/// entre Catalog y Storage (CAT-05): ningún módulo de negocio referencia al otro, y el
/// composition root —que ya referencia a los dos— es el único lugar donde ese acoplamiento es
/// legítimo.
///
/// No decide nada: la regla de negocio (US-1/US-18, CUC presente y cliente activo) es de
/// <c>QuotationCustomerEligibility</c>, en Application.
/// </summary>
internal sealed class QuotationCustomerLookup(ICustomerRepository repository)
    : IQuotationCustomerLookup
{
    public async Task<QuotationCustomerRef?> FindAsync(
        Guid tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        var customer = await repository.FindAsync(
            tenantId, new CustomerId(clientId), cancellationToken);
        return customer is null
            ? null
            : new QuotationCustomerRef(
                customer.Id.Value,
                customer.TenantId,
                customer.Cuc,
                customer.IsActive,
                customer.Name,
                customer.Phone,
                customer.Address,
                customer.WithRetention,
                customer.VatSurplus);
    }

    public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationAsync(
        Guid tenantId, string term, CancellationToken cancellationToken) =>
        repository.SearchIdsByIdentificationNumberAsync(tenantId, term, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken cancellationToken)
    {
        var names = await repository.FindNamesByIdsAsync(
            tenantId,
            clientIds.Select(id => new CustomerId(id)).ToArray(),
            cancellationToken);

        return names.ToDictionary(entry => entry.Key.Value, entry => entry.Value);
    }
}
