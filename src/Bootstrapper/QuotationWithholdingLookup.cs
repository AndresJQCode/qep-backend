using BuildingBlocks.Domain.Taxes;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta Catalog (tasas y unidad tributaria) y Customers (tasas asignadas) al puerto con el que <c>quotations</c>
/// congela retenciones (spec impuestos por tipo §7.5). Vive acá por el mismo motivo que
/// <see cref="QuotationCustomerLookup"/>: Quotations no referencia ni a Catalog ni a Customers. No decide si una
/// tasa sirve para un selector (eso es <see cref="WithholdingSelection"/>): sólo arma la foto.
/// </summary>
internal sealed class QuotationWithholdingLookup(
    ICustomerRepository customers,
    ITaxRateRepository taxRates,
    ITaxUnitValueRepository taxUnitValues)
    : IQuotationWithholdingLookup
{
    public async Task<IReadOnlyList<WithholdingRate>> ForCustomerAsync(
        Guid tenantId, Guid customerId, string? country, DateOnly documentDate, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
        if (customer is null || customer.WithholdingTaxRateIds.Count == 0)
        {
            return [];
        }

        // El código canónico: la tasa guarda el país normalizado y el repositorio de UVT compara crudo.
        var wantedCountry = country is null ? null : TaxCountries.Find(country)?.Country ?? country.Trim().ToUpperInvariant();
        var byId = (await taxRates.ListAsync(tenantId, cancellationToken)).ToDictionary(rate => rate.Id.Value);

        // En el orden del cliente. Una tasa borrada del catálogo ya no tiene qué congelar; una inactiva o que no retiene
        // no debería estar asignada (Customers lo valida y Catalog no deja desactivarla), pero no se congela si llega.
        var assigned = customer.WithholdingTaxRateIds
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<TaxRate>()
            .Where(rate => rate.IsActive && rate.Withholds)
            .Where(rate => wantedCountry is null || rate.Country == wantedCountry)
            .ToArray();

        return await PhotosAsync(assigned, documentDate, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, WithholdingRateCandidate>> ForRatesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> taxRateIds, DateOnly documentDate, CancellationToken cancellationToken)
    {
        if (taxRateIds.Count == 0)
        {
            return new Dictionary<Guid, WithholdingRateCandidate>();
        }

        // Una tasa por tenant se cuenta con los dedos de una mano (ver ITaxRateRepository): se lee el tenant entero.
        var wanted = taxRateIds.ToHashSet();
        var found = (await taxRates.ListAsync(tenantId, cancellationToken))
            .Where(rate => wanted.Contains(rate.Id.Value))
            .ToArray();
        var photos = await PhotosAsync(found, documentDate, cancellationToken);

        return found
            .Zip(photos, (rate, photo) => new WithholdingRateCandidate(photo, rate.IsActive, rate.Withholds))
            .ToDictionary(candidate => candidate.Rate.TaxRateId);
    }

    // La UVT se lee sólo si alguna tasa tiene base mínima en un país con unidad, y una sola vez para todas.
    private async Task<IReadOnlyList<WithholdingRate>> PhotosAsync(
        IReadOnlyList<TaxRate> rates, DateOnly documentDate, CancellationToken cancellationToken)
    {
        var needsUnit = rates.Any(rate => rate.MinimumBase is not null && UnitOf(rate) is not null);
        IReadOnlyList<TaxUnitValue> values = needsUnit
            ? await taxUnitValues.ListAsync(null, cancellationToken)
            : [];

        return [.. rates.Select(rate => Photo(rate, values, documentDate))];
    }

    private static WithholdingRate Photo(TaxRate rate, IReadOnlyList<TaxUnitValue> values, DateOnly documentDate)
    {
        var unit = rate.MinimumBase is null ? null : UnitOf(rate);
        return new WithholdingRate(
            rate.Id.Value,
            rate.Name,
            rate.Type,
            rate.Country,
            rate.Percentage,
            rate.MinimumBase,
            unit,
            // Sin unidad (base en dinero) o sin base no hay nada que congelar; sin un año cargado queda null y la
            // tasa no aplica (D20).
            unit is null ? null : TaxUnitValues.ValueFor(values, rate.Country, documentDate.Year));
    }

    private static string? UnitOf(TaxRate rate) => TaxCountries.Find(rate.Country)?.TaxUnitName;
}
