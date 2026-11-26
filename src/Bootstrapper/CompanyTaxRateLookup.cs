using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Companies.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el repositorio de tasas de <c>Catalog</c> al puerto con el que <c>companies</c> valida y pinta las tasas
/// municipales de una empresa (spec ReteICA por empresa §7.3). Calcado de <see cref="CustomerTaxRateLookup"/>: no decide
/// nada, sólo traduce; si la tasa sirve lo decide <c>CompanyWithholdingRules</c>.
/// </summary>
internal sealed class CompanyTaxRateLookup(ITaxRateRepository repository) : ICompanyTaxRateLookup
{
    public async Task<IReadOnlyDictionary<Guid, CompanyTaxRateRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, CancellationToken cancellationToken)
    {
        if (taxRateIds.Count == 0)
        {
            return new Dictionary<Guid, CompanyTaxRateRef>();
        }

        // Un tenant tiene un puñado de tasas (ver ITaxRateRepository): se lee el tenant entero y se filtra en memoria.
        var wanted = taxRateIds.ToHashSet();
        var rates = await repository.ListAsync(tenantId, cancellationToken);
        return rates
            .Where(rate => wanted.Contains(rate.Id.Value))
            .ToDictionary(rate => rate.Id.Value, ToRef);
    }

    private static CompanyTaxRateRef ToRef(TaxRate rate) =>
        new(
            rate.Id.Value, rate.Name, rate.Type, rate.Country, rate.Percentage, rate.IsActive, rate.Withholds, rate.CityId,
            rate.AppliesTo);
}
