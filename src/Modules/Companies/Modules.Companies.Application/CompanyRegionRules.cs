using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>
/// La parte de la regla de región que necesita preguntarle a Geography (spec 2026-11-23): el validador ya rechazó la región
/// con Colombia, y acá se comprueba que la región exista y sea del país de la empresa.
///
/// Un país sin catálogo de regiones no tiene filas en <c>geography.regions</c>, así que falla igual que una región
/// inexistente o de otro país, sin que el módulo conozca el catálogo de países. Lo mismo un id de departamento
/// colombiano mandado como región: <c>GET /countries/CO/regions</c> devuelve departamentos.
/// </summary>
internal static class CompanyRegionRules
{
    public const string CompanyRegionInvalid = "companies.company.region_invalid";

    /// <summary>
    /// Devuelve la región resuelta, o <c>null</c> si no vino ninguna. Lanza un 422 cuando la región no existe o no
    /// pertenece al país.
    /// </summary>
    public static async Task<CompanyRegionRef?> ResolveRegionAsync(
        ICompanyGeographyLookup lookup,
        string country,
        Guid? regionId,
        CancellationToken cancellationToken)
    {
        if (regionId is not { } id)
        {
            return null;
        }

        var found = await lookup.FindRegionsAsync([id], cancellationToken);
        if (!found.TryGetValue(id, out var region)
            || !string.Equals(region.CountryCode, country, StringComparison.Ordinal))
        {
            throw new CompaniesDomainException(
                CompanyRegionInvalid,
                "The region does not exist or does not belong to the country.");
        }

        return region;
    }
}
