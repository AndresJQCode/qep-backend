using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// La parte de la regla de region que necesita preguntarle a Geography (spec 2026-11-23): el validador ya rechazó la
/// región con Colombia, y acá se comprueba que la región exista y sea del país del domicilio.
///
/// Un país sin catálogo de regiones no tiene filas en <c>geography.regions</c>, así que falla igual que una región
/// inexistente o de otro país, sin que el módulo conozca el catálogo de países. Lo mismo un id de departamento
/// colombiano mandado como región: <c>GET /countries/CO/regions</c> devuelve departamentos, y el frontend puede
/// conservar uno al cambiar de país.
/// </summary>
internal static class CustomerRegionRules
{
    public const string CustomerRegionInvalid = "customers.customer.region_invalid";

    public const string AddressRegionInvalid = "customers.address.region_invalid";

    public const string BillingProfileRegionInvalid = "customers.billing_profile.region_invalid";

    /// <summary>
    /// Devuelve la región resuelta, o <c>null</c> si no vino ninguna. Lanza un 422 con el mapa <c>errors</c> (clave
    /// <c>RegionId</c>) cuando la región no existe o no pertenece al país.
    /// </summary>
    public static async Task<CustomerRegionRef?> ResolveRegionAsync(
        ICustomerGeographyLookup lookup,
        string? country,
        Guid? regionId,
        string invalidCode,
        CancellationToken cancellationToken)
    {
        if (regionId is not { } id)
        {
            return null;
        }

        var found = await lookup.FindRegionsAsync([id], cancellationToken);
        var normalizedCountry = country?.Trim().ToUpperInvariant();
        if (!found.TryGetValue(id, out var region)
            || !string.Equals(region.CountryCode, normalizedCountry, StringComparison.Ordinal))
        {
            throw new CustomersDomainException(
                invalidCode,
                "The region does not exist or does not belong to the country.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["RegionId"] = ["The region does not exist or does not belong to the country."]
                });
        }

        return region;
    }
}
