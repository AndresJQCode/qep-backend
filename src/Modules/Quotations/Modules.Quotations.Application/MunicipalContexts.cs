using BuildingBlocks.Domain.Taxes;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el <see cref="MunicipalContext"/> que el agregado consume (plan P4, spec ReteICA por empresa §7.5): el agregado no
/// consulta, así que la aplicación calcula primero el municipio resultante (<c>OperationCityFor</c> /
/// <c>OperationCityAtCreation</c>) y acá pregunta, para ese municipio, qué tasa ofrece la empresa por cada tipo municipal
/// de su país y si el cliente es agente ahí. Una sola regla para la cotización, el pedido y sus vistas previas.
/// </summary>
public static class MunicipalContexts
{
    /// <param name="operationCityId">El municipio resultante; null sin municipio.</param>
    /// <param name="explicitCity">Si el cuerpo trajo un <c>operationCityId</c> explícito: sólo ése se comprueba contra
    /// Geography (el de la empresa ya existe).</param>
    /// <param name="codePrefix"><c>quotation</c> u <c>order</c>.</param>
    public static async Task<MunicipalContext> ResolveAsync(
        IQuotationWithholdingLookup lookup,
        IQuotationGeographyLookup geography,
        Guid tenantId,
        Guid customerId,
        QuotationBillingAccount? billingAccount,
        Guid? operationCityId,
        bool explicitCity,
        DateOnly documentDate,
        string codePrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(geography);

        // A7: una empresa de un país sin tipos municipales no tiene municipio; lo que llegue se ignora y no se valida.
        if (billingAccount is null || TaxCountries.MunicipalTypes(billingAccount.Country?.Trim()).Count == 0)
        {
            return MunicipalContext.None;
        }

        if (explicitCity && operationCityId is { } chosen)
        {
            var found = await geography.FindCitiesAsync([chosen], cancellationToken);
            if (!found.ContainsKey(chosen))
            {
                throw new QuotationsDomainException(
                    $"{codePrefix}.billing.operation_city_invalid",
                    "The operation municipality does not exist.");
            }
        }

        return await ForCityAsync(lookup, tenantId, customerId, billingAccount, operationCityId, documentDate, cancellationToken);
    }

    /// <summary>
    /// El contexto para un municipio que no hace falta comprobar (el de la empresa, o uno ya comprobado): nunca lanza por
    /// la resolución municipal. Lo usan también los pedidos que nacen sin cotización (<see cref="OrderCustomerWithholdings"/>).
    /// </summary>
    internal static async Task<MunicipalContext> ForCityAsync(
        IQuotationWithholdingLookup lookup,
        Guid tenantId,
        Guid customerId,
        QuotationBillingAccount? billingAccount,
        Guid? operationCityId,
        DateOnly documentDate,
        CancellationToken cancellationToken)
    {
        var country = billingAccount?.Country?.Trim();
        var types = TaxCountries.MunicipalTypes(country);
        if (billingAccount is null || types.Count == 0)
        {
            return MunicipalContext.None;
        }

        // Sin municipio no hay tasa que buscar, pero cada tipo sigue con su oferta vacía: el agregado sabe así qué filas
        // municipales quitar.
        if (operationCityId is not { } cityId)
        {
            return new MunicipalContext(null, [.. types.Select(type => new MunicipalOffer(type.Type, null, false))]);
        }

        var companyRates = await lookup.ForCompanyMunicipalAsync(
            tenantId, billingAccount.CompanyId, country!, cityId, documentDate, cancellationToken);
        var offers = new List<MunicipalOffer>(types.Count);
        foreach (var type in types)
        {
            var companyRate = companyRates.FirstOrDefault(rate => string.Equals(rate.Type, type.Type, StringComparison.Ordinal));
            var isAgent = await lookup.IsWithholdingAgentAsync(
                tenantId, customerId, country!, type.Type, cityId, cancellationToken);
            offers.Add(new MunicipalOffer(type.Type, companyRate, isAgent));
        }

        return new MunicipalContext(cityId, offers);
    }
}
