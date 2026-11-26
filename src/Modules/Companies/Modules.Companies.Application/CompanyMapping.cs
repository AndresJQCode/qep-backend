using Modules.Companies.Domain;

namespace Modules.Companies.Application;

internal static class CompanyMapping
{
    /// <summary>
    /// Del agregado al DTO, con la ciudad ya resuelta por el llamador. No la resuelve esta
    /// funcion: cada handler decide como (una consulta puntual en Get/Create/Update, un lote en
    /// List) y esto solo ensambla — mismo criterio que <c>CustomerMapping.ToDto</c>.
    ///
    /// <paramref name="withholdingTaxRates"/> llega ya resuelto (ver <see cref="ResolveWithholdingTaxRatesAsync"/>); el
    /// listado no lo pide porque la grilla no lo pinta, y ahí queda vacío.
    /// </summary>
    public static CompanyDto ToDto(
        this Company company,
        CompanyCityRef? city,
        IReadOnlyList<CompanyWithholdingTaxRateDto>? withholdingTaxRates = null,
        CompanyRegionRef? region = null) => new(
        company.Id.Value,
        company.Name,
        company.BankAccounts
            .Select(account => new CompanyBankAccountPayload(
                account.BankName,
                account.AccountNumber,
                account.Currency))
            .ToArray(),
        company.TaxId,
        company.Country,
        company.DocumentType.ToWire(),
        company.IsActive,
        company.Phone,
        company.Email,
        company.Address,
        city is null ? null : new CompanyCityDto(city.CityId, city.CityDivipolaCode, city.CityName),
        city is null
            ? null
            : new CompanyDepartmentDto(
                city.DepartmentId, city.DepartmentDivipolaCode, city.DepartmentName),
        company.CityName,
        company.CreatedAt,
        company.UpdatedAt,
        withholdingTaxRates ?? [],
        company.ChargesVat,
        region is null ? null : new CompanyRegionDto(region.Id, region.Name));

    /// <summary>
    /// La version de una sola empresa: resuelve su ciudad y sus tasas municipales y arma el DTO. Para
    /// <c>GetCompanyHandler</c>, <c>DeactivateCompanyHandler</c> y <c>ActivateCompanyHandler</c>,
    /// que ya tienen la <c>Company</c> en mano y no necesitan resolver nada mas antes.
    ///
    /// La FK de base garantiza que la ciudad exista, asi que un miss aca es corrupcion de datos
    /// y no una entrada de usuario invalida — por eso lanza <see cref="InvalidOperationException"/>
    /// (500) y no un <see cref="CompaniesDomainException"/> (422): no hay ningun campo del
    /// request que el llamador pueda corregir.
    /// </summary>
    public static async Task<CompanyDto> ToDtoAsync(
        this Company company,
        ICompanyGeographyLookup geographyLookup,
        ICompanyTaxRateLookup taxRateLookup,
        CancellationToken cancellationToken)
    {
        var rates = await taxRateLookup.FindManyAsync(
            company.TenantId, company.WithholdingTaxRateIds, cancellationToken);
        var withholdingTaxRates = await ResolveWithholdingTaxRatesAsync(
            company.WithholdingTaxRateIds, rates, geographyLookup, cancellationToken);

        // Spec 2026-11-23: una región que Geography ya no devuelve se omite en vez de tirar la ficha (referencia blanda).
        CompanyRegionRef? region = null;
        if (company.RegionId is { } regionId)
        {
            (await geographyLookup.FindRegionsAsync([regionId], cancellationToken)).TryGetValue(regionId, out region);
        }

        // Fuera de Colombia no hay CityId que resolver: la ciudad es texto libre en CityName.
        if (company.CityId is not { } cityId)
        {
            return company.ToDto(null, withholdingTaxRates, region);
        }

        var city = await geographyLookup.FindCityAsync(cityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"City '{company.CityId}' referenced by company '{company.Id}' was not found.");

        return company.ToDto(city, withholdingTaxRates, region);
    }

    /// <summary>
    /// Las tasas municipales de la empresa con sus municipios, resueltos en una sola consulta a Geography. Ordenadas por
    /// departamento, municipio y tipo (Ordinal), como las pinta la ficha. Un id que el catálogo ya no tiene no se pinta.
    /// </summary>
    public static async Task<IReadOnlyList<CompanyWithholdingTaxRateDto>> ResolveWithholdingTaxRatesAsync(
        IReadOnlyCollection<Guid> taxRateIds,
        IReadOnlyDictionary<Guid, CompanyTaxRateRef> found,
        ICompanyGeographyLookup geographyLookup,
        CancellationToken cancellationToken)
    {
        var rates = taxRateIds.Distinct()
            .Where(found.ContainsKey)
            .Select(id => found[id])
            .ToArray();
        if (rates.Length == 0)
        {
            return [];
        }

        var cityIds = rates.Where(rate => rate.CityId is not null).Select(rate => rate.CityId!.Value).Distinct().ToArray();
        var cities = await geographyLookup.FindCitiesAsync(cityIds, cancellationToken);

        return rates
            .Select(rate => new CompanyWithholdingTaxRateDto(
                rate.Id,
                rate.Name,
                rate.Type,
                rate.Country,
                rate.Percentage,
                rate.IsActive,
                rate.CityId is { } cityId && cities.TryGetValue(cityId, out var city)
                    ? new CompanyMunicipalityDto(city.CityId, city.CityName, city.DepartmentId, city.DepartmentName)
                    : null,
                rate.AppliesTo.ToString()))
            .OrderBy(rate => rate.City?.DepartmentName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(rate => rate.City?.Name ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(rate => rate.Type, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Del contrato HTTP al dominio. No normaliza ni valida nada: eso es trabajo de
    /// <see cref="CompanyBankAccount.Normalized"/>, y hacerlo tambien aca crearia una segunda
    /// definicion de "cuenta valida" que el dia que difiera nadie va a notar.
    ///
    /// El <c>?? []</c> cubre la lista ausente del JSON. El validador ya la rechaza con su mapa de
    /// errores por campo y corre antes que esto, asi que en la practica no llega; esta igual para
    /// que un llamador futuro que se saltee el validador falle como 422 del dominio y no como
    /// NullReferenceException.
    /// </summary>
    public static IReadOnlyCollection<CompanyBankAccount> ToDomain(
        this IReadOnlyList<CompanyBankAccountPayload>? payloads) =>
        (payloads ?? [])
            .Select(payload => new CompanyBankAccount
            {
                BankName = payload.BankName,
                AccountNumber = payload.AccountNumber,
                Currency = payload.Currency
            })
            .ToArray();
}
