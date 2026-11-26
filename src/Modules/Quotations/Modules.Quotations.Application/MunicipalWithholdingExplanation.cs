using BuildingBlocks.Domain.Taxes;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Lo que el compositor agrega a la respuesta de un documento para explicar la retención municipal.</summary>
public sealed record MunicipalExplanation(
    QuotationCityResponse? OperationCity,
    QuotationCityResponse? BillingCompanyCity,
    IReadOnlyList<MunicipalWithholdingResponse> Entries)
{
    public static MunicipalExplanation None { get; } = new(null, null, []);
}

/// <summary>
/// Explica al leer la retención municipal de un documento (spec ReteICA por empresa §9.4, plan P7): el municipio de la
/// operación y el de la empresa con nombre, y por cada tipo municipal del país de la empresa la tasa que la empresa tiene
/// hoy ahí y por qué quedó o no una fila. Una sola regla para los dos compositores y sus vistas previas: la pantalla no ve
/// las asignaciones de la empresa ni la lista de agentes del cliente, así que el porqué nunca se calcula en el navegador.
///
/// Leer no mueve nada (baseline D17, A14): la foto y los totales son los guardados; esto sólo explica con los datos de hoy.
/// </summary>
public static class MunicipalWithholdingExplanation
{
    /// <param name="ownBilling">Si la fuente vigente es la de facturación propia de la cotización: el tercero no tiene lista
    /// de agentes, así que <c>customerIsAgent</c> va null.</param>
    /// <param name="activeRows">Las filas de la fuente vigente, apliquen o no por base mínima.</param>
    /// <param name="documentDate">La fecha con que se arma la foto de la tasa de la empresa; sólo se leen su nombre y su
    /// porcentaje, así que basta la fecha UTC de creación.</param>
    public static async Task<MunicipalExplanation> ExplainAsync(
        IQuotationWithholdingLookup lookup,
        IQuotationGeographyLookup geography,
        Guid tenantId,
        Guid customerId,
        QuotationBillingAccountDto? billingAccount,
        Guid? operationCityId,
        bool ownBilling,
        IReadOnlyList<WithholdingTaxRateDto> activeRows,
        DateOnly documentDate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(geography);
        ArgumentNullException.ThrowIfNull(activeRows);

        // Sin empresa o con una de un país sin tipos municipales no hay nada que explicar (§9.4).
        var country = TaxCountries.Find(billingAccount?.Country)?.Country;
        var types = TaxCountries.MunicipalTypes(country);
        if (billingAccount is null || country is null || types.Count == 0)
        {
            return MunicipalExplanation.None;
        }

        // Una sola lectura a Geography para los dos municipios.
        Guid[] cityIds = [.. new[] { operationCityId, billingAccount.CityId }.OfType<Guid>().Distinct()];
        IReadOnlyDictionary<Guid, QuotationCityRef> cities = cityIds.Length == 0
            ? new Dictionary<Guid, QuotationCityRef>()
            : await geography.FindCitiesAsync(cityIds, cancellationToken);

        IReadOnlyList<WithholdingRate> companyRates = operationCityId is { } cityId
            ? await lookup.ForCompanyMunicipalAsync(
                tenantId, billingAccount.CompanyId, country, cityId, documentDate, cancellationToken)
            : [];

        var entries = new List<MunicipalWithholdingResponse>(types.Count);
        foreach (var type in types.Select(definition => definition.Type))
        {
            var hasRow = activeRows.Any(row => string.Equals(row.Type, type, StringComparison.Ordinal));
            var companyRate = companyRates.FirstOrDefault(rate => string.Equals(rate.Type, type, StringComparison.Ordinal));
            bool? customerIsAgent = ownBilling || operationCityId is not { } city
                ? null
                : await lookup.IsWithholdingAgentAsync(tenantId, customerId, country, type, city, cancellationToken);
            var status = MunicipalWithholdings.Describe(
                hasRow, operationCityId, companyRate is not null, customerIsAgent, ownBilling);
            entries.Add(new MunicipalWithholdingResponse(
                type,
                country,
                companyRate is null
                    ? null
                    : new MunicipalCompanyRateResponse(companyRate.TaxRateId, companyRate.Name, companyRate.Percentage),
                status.ToString(),
                customerIsAgent));
        }

        return new MunicipalExplanation(
            ToResponse(operationCityId, cities), ToResponse(billingAccount.CityId, cities), entries);
    }

    /// <summary>La fecha con que se explica un documento: su creación en UTC (plan P7).</summary>
    public static DateOnly DateOf(DateTimeOffset createdAt) => DateOnly.FromDateTime(createdAt.UtcDateTime);

    private static QuotationCityResponse? ToResponse(Guid? cityId, IReadOnlyDictionary<Guid, QuotationCityRef> cities) =>
        cityId is { } id && cities.TryGetValue(id, out var city)
            ? new QuotationCityResponse(city.Id, city.Name, city.DepartmentId, city.DepartmentName)
            : null;
}
