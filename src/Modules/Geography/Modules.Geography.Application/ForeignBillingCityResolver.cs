using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public enum ForeignCityResolutionKind
{
    /// <summary>El país no está en el catálogo de Siigo: no hay ciudad que elegir y la bandeja no aplica.</summary>
    CountryNotInCatalog,

    /// <summary>Una sola ciudad del catálogo, por nombre o por sinónimo vivo.</summary>
    Matched,

    /// <summary>El país está, pero el texto no coincide con una sola ciudad: va a la bandeja del operador.</summary>
    Unresolved,
}

/// <summary>Los tres códigos con los que Siigo identifica la ciudad, tal cual vienen en su lista (<c>Us</c>, <c>09</c>, <c>1</c>).</summary>
public sealed record BillingCityCodes(string SiigoCountryCode, string StateCode, string CityCode);

/// <summary><see cref="Codes"/> viene sólo con <see cref="ForeignCityResolutionKind.Matched"/>.</summary>
public sealed record ForeignCityResolution(ForeignCityResolutionKind Kind, BillingCityCodes? Codes);

/// <summary>Sólo lee. Lo llama Bootstrapper al armar el pedido para Siigo; global, sin IExecutionContext.</summary>
public interface IForeignBillingCityResolver
{
    /// <summary><paramref name="countryCode"/> llega recortado y en mayúscula; <paramref name="cityText"/> es lo que escribió la
    /// persona, sin normalizar.</summary>
    Task<ForeignCityResolution> ResolveAsync(string countryCode, string cityText, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-11-26 «Cómo se resuelve una ciudad». Un nombre repetido dentro de un país (Boston, Berlín) nunca coincide solo:
/// una factura no sale con una ciudad que nadie eligió. Sólo un sinónimo vivo, que alguien confirmó, resuelve esos casos.
/// </summary>
public sealed class ForeignBillingCityResolver(
    IBillingCityRepository cities, IForeignCitySynonymRepository synonyms) : IForeignBillingCityResolver
{
    public async Task<ForeignCityResolution> ResolveAsync(
        string countryCode, string cityText, CancellationToken cancellationToken)
    {
        if (!await cities.CountryExistsAsync(countryCode, cancellationToken))
        {
            return new ForeignCityResolution(ForeignCityResolutionKind.CountryNotInCatalog, null);
        }

        var key = UnresolvedForeignCity.KeyFor(cityText);
        if (key.Length == 0)
        {
            return Unresolved;
        }

        var byName = await cities.MatchByNameAsync(countryCode, key, cancellationToken);
        if (byName.Count == 1)
        {
            return Matched(byName[0]);
        }

        var synonym = await synonyms.FindActiveAsync(countryCode, key, cancellationToken);
        if (synonym is not null && await cities.FindAsync(synonym.BillingCityId, cancellationToken) is { } chosen)
        {
            return Matched(chosen);
        }

        return Unresolved;
    }

    private static ForeignCityResolution Unresolved { get; } = new(ForeignCityResolutionKind.Unresolved, null);

    private static ForeignCityResolution Matched(BillingCity city) =>
        new(
            ForeignCityResolutionKind.Matched,
            new BillingCityCodes(city.SiigoCountryCode, city.StateCode, city.CityCode));
}
