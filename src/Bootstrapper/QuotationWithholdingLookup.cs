using BuildingBlocks.Domain.Taxes;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta Catalog (tasas y unidad tributaria), Customers (tasas asignadas y municipios donde es agente) y Companies (tasas
/// municipales de la empresa, spec ReteICA por empresa §7.5) al puerto con el que <c>quotations</c> congela retenciones
/// (spec impuestos por tipo §7.5). Vive acá por el mismo motivo que
/// <see cref="QuotationCustomerLookup"/>: Quotations no referencia ni a Catalog ni a Customers. No decide si una
/// tasa sirve para un selector (eso es <see cref="WithholdingSelection"/>): sólo arma la foto.
/// <para>
/// Scoped a propósito: memoiza el cliente y la empresa que lee, y el memo vive lo que el request. El composer pide el
/// mismo cliente dos o tres veces (<c>MunicipalWithholdingExplanation.cs:66-72</c>, <c>MunicipalContexts.cs:83-86</c>,
/// <c>QuotationDraftBuilder.cs:76</c>), y en producción cada lectura en serie paga la red hasta la base. El memo guarda
/// la instancia que devolvió el repositorio, no una copia, así que ve lo que el mismo scope le cambie.
/// </para>
/// </summary>
internal sealed class QuotationWithholdingLookup(
    ICustomerRepository customers,
    ICompanyRepository companies,
    ITaxRateRepository taxRates,
    ITaxUnitValueRepository taxUnitValues)
    : IQuotationWithholdingLookup
{
    private readonly Dictionary<(Guid TenantId, Guid CustomerId), Customer?> customerMemo = [];
    private readonly Dictionary<(Guid TenantId, Guid CompanyId), Company?> companyMemo = [];

    public async Task<IReadOnlyList<WithholdingRate>> ForCompanyMunicipalAsync(
        Guid tenantId, Guid companyId, string country, Guid cityId, DateOnly documentDate, CancellationToken cancellationToken)
    {
        var company = await FindCompanyAsync(tenantId, companyId, cancellationToken);
        if (company is null || company.WithholdingTaxRateIds.Count == 0)
        {
            return [];
        }

        var wantedCountry = Canonical(country);
        var assignedIds = company.WithholdingTaxRateIds.ToHashSet();
        // Primero el país y después si es municipal: preguntar por el alcance de un país que la tabla no conoce no tiene
        // sentido. Una inactiva asignada (A16: se conserva al reenviarla) no se congela en un documento nuevo.
        var offered = (await taxRates.ListAsync(tenantId, cancellationToken))
            .Where(rate => assignedIds.Contains(rate.Id.Value))
            .Where(rate => rate.Country == wantedCountry && rate.CityId == cityId)
            .Where(rate => rate.IsActive && Withholds(rate) && TaxCountries.IsMunicipal(rate.Country, rate.Type))
            .ToArray();

        return await PhotosAsync(offered, documentDate, cancellationToken);
    }

    public async Task<bool> IsWithholdingAgentAsync(
        Guid tenantId, Guid customerId, string country, string type, Guid cityId, CancellationToken cancellationToken)
    {
        var customer = await FindCustomerAsync(tenantId, customerId, cancellationToken);
        return customer is not null && customer.IsWithholdingAgent(Canonical(country), type, cityId);
    }

    public async Task<IReadOnlyDictionary<string, TaxUnitPhoto>> TaxUnitsAsync(
        IReadOnlyCollection<string> countries, DateOnly documentDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(countries);
        var units = new Dictionary<string, TaxUnitPhoto>(StringComparer.Ordinal);
        if (countries.Count == 0)
        {
            return units;
        }

        // Una sola lectura para todos los países, como la foto (PhotosAsync).
        var values = await taxUnitValues.ListAsync(null, cancellationToken);
        foreach (var country in countries.Select(Canonical).Distinct(StringComparer.Ordinal))
        {
            if (TaxUnitValues.Resolve(values, country, documentDate.Year) is { } resolved)
            {
                units[country] = new TaxUnitPhoto(resolved.Value, resolved.Year);
            }
        }

        return units;
    }

    // El código canónico: la tasa guarda el país normalizado y el repositorio de UVT compara crudo.
    private static string Canonical(string country) =>
        TaxCountries.Find(country)?.Country ?? country.Trim().ToUpperInvariant();

    public async Task<IReadOnlyList<WithholdingRate>> ForCustomerAsync(
        Guid tenantId, Guid customerId, string? country, DateOnly documentDate, CancellationToken cancellationToken)
    {
        var customer = await FindCustomerAsync(tenantId, customerId, cancellationToken);
        if (customer is null || customer.WithholdingTaxRateIds.Count == 0)
        {
            return [];
        }

        var wantedCountry = country is null ? null : Canonical(country);
        var byId = (await taxRates.ListAsync(tenantId, cancellationToken)).ToDictionary(rate => rate.Id.Value);

        // En el orden del cliente. Una tasa borrada del catálogo ya no tiene qué congelar; una inactiva o que no retiene
        // no debería estar asignada (Customers lo valida y Catalog no deja desactivarla), pero no se congela si llega.
        // Primero el país: preguntar si retiene una tasa de un país que la tabla no conoce lanzaría. Una municipal
        // tampoco entra (A15): la ReteICA es de la empresa y el municipio, no del cliente.
        var assigned = customer.WithholdingTaxRateIds
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<TaxRate>()
            .Where(rate => wantedCountry is null || rate.Country == wantedCountry)
            .Where(rate => rate.IsActive && Withholds(rate) && !TaxCountries.IsMunicipal(rate.Country, rate.Type))
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
            .Zip(photos, (rate, photo) => new WithholdingRateCandidate(photo, rate.IsActive, Withholds(rate)))
            .ToDictionary(candidate => candidate.Rate.TaxRateId);
    }

    // Un id que no existe también se memoiza: preguntar otra vez en el mismo request daría lo mismo.
    private async Task<Customer?> FindCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        if (!customerMemo.TryGetValue((tenantId, customerId), out var customer))
        {
            customer = await customers.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
            customerMemo[(tenantId, customerId)] = customer;
        }

        return customer;
    }

    private async Task<Company?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        if (!companyMemo.TryGetValue((tenantId, companyId), out var company))
        {
            company = await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken);
            companyMemo[(tenantId, companyId)] = company;
        }

        return company;
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
        // Sin unidad (base en dinero) o sin base no hay nada que congelar; sin un año cargado queda null y la tasa no
        // aplica (D20). El año viaja con el valor para mostrar una UVT desactualizada (spec retenciones por UVT §5.2).
        var taxUnit = unit is null ? null : TaxUnitValues.Resolve(values, rate.Country, documentDate.Year);
        return new WithholdingRate(
            rate.Id.Value,
            rate.Name,
            rate.Type,
            rate.Country,
            rate.Percentage,
            rate.MinimumBase,
            unit,
            taxUnit?.Value,
            // El municipio de una municipal viaja en la foto: las reglas de la cotización y el pedido comparan con él
            // (spec ReteICA por empresa §7.5).
            rate.CityId)
        {
            TaxUnitYear = taxUnit?.Year,
        };
    }

    // TaxRate.Withholds lanza con un país o un tipo que la tabla no conoce; acá eso es «no retiene», y el selector lo
    // rechaza como inválido en vez de tumbar el guardado con un 500.
    private static bool Withholds(TaxRate rate) =>
        TaxCountries.FindType(rate.Country, rate.Type)?.Behavior == TaxBehavior.Withholds;

    private static string? UnitOf(TaxRate rate) => TaxCountries.Find(rate.Country)?.TaxUnitName;
}
