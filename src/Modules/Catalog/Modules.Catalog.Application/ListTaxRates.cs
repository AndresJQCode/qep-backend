using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <param name="Types">Si viene y no está vacío, sólo esos tipos; null o vacío, todas las tasas.</param>
public sealed record ListTaxRatesQuery(Guid TenantId, IReadOnlyCollection<string>? Types = null)
    : IQuery<IReadOnlyList<TaxRateDto>>;

public sealed class ListTaxRatesHandler(
    ITaxRateRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListTaxRatesQuery, IReadOnlyList<TaxRateDto>>
{
    public async Task<IReadOnlyList<TaxRateDto>> HandleAsync(
        ListTaxRatesQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.TaxRateRead);

        var taxRates = await repository.ListAsync(query.TenantId, cancellationToken);

        // Se filtra en memoria: un tenant tiene un puñado de tasas (ver ITaxRateRepository), así que
        // una consulta con filtro no resuelve un problema que exista.
        var filtered = query.Types is { Count: > 0 } types
            ? taxRates.Where(taxRate => types.Contains(taxRate.Type, StringComparer.Ordinal))
            : taxRates;

        return filtered.Select(taxRate => taxRate.ToDto()).ToArray();
    }
}
