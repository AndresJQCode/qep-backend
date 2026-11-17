using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Customers.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de tasas de <c>Catalog</c> al puerto con el que <c>customers</c> valida y pinta las retenciones de
/// un cliente (spec impuestos por tipo §7.4). Mismo aislamiento que <see cref="QuotationTaxRateLookup"/>: no decide nada,
/// sólo traduce; si la tasa sirve (activa, retiene) lo decide Customers.
/// </summary>
internal sealed class CustomerTaxRateLookup(ITaxRateRepository repository) : ICustomerTaxRateLookup
{
    public async Task<IReadOnlyDictionary<Guid, CustomerTaxRateRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken)
    {
        if (taxRateIds.Count == 0)
        {
            return new Dictionary<Guid, CustomerTaxRateRef>();
        }

        // Una tasa por tenant se cuenta con los dedos de una mano (ver ITaxRateRepository): se lee el tenant entero y se
        // filtra en memoria, que es una consulta sin parámetros por lote.
        var wanted = taxRateIds.ToHashSet();
        var rates = await repository.ListAsync(tenantId, cancellationToken);
        return rates
            .Where(rate => wanted.Contains(rate.Id.Value))
            .ToDictionary(rate => rate.Id.Value, ToRef);
    }

    public async Task<IReadOnlyList<CustomerTaxRateRef>> ListWithholdingAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        [.. (await repository.ListAsync(tenantId, cancellationToken))
            .Where(rate => rate.IsActive && rate.Withholds)
            .Select(ToRef)];

    private static CustomerTaxRateRef ToRef(TaxRate rate) =>
        new(rate.Id.Value, rate.Name, rate.Type, rate.Country, rate.Percentage, rate.IsActive, rate.Withholds);
}
