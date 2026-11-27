using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Un país del catálogo de Siigo: <see cref="Code"/> en mayúscula y <see cref="Name"/> como lo escribe la lista.</summary>
public sealed record BillingCountry(string Code, string Name);

/// <summary>El catálogo de ciudades de Siigo fuera de Colombia. Sólo lectura y sin seguimiento: lo escribe el importador.</summary>
public interface IBillingCityRepository
{
    /// <summary>Si el catálogo tiene al menos una ciudad de ese país (<paramref name="countryCode"/> en mayúscula).</summary>
    Task<bool> CountryExistsAsync(string countryCode, CancellationToken cancellationToken);

    /// <summary>Las ciudades del país cuyo nombre normalizado es exactamente <paramref name="normalizedName"/>. El catálogo
    /// repite nombres, así que pueden ser varias.</summary>
    Task<IReadOnlyList<BillingCity>> MatchByNameAsync(
        string countryCode, string normalizedName, CancellationToken cancellationToken);

    Task<BillingCity?> FindAsync(BillingCityId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<BillingCity>> ListByIdsAsync(
        IReadOnlyCollection<BillingCityId> ids, CancellationToken cancellationToken);

    /// <summary>Todas las ciudades de esos países: lo que alimenta las sugerencias de la bandeja.</summary>
    Task<IReadOnlyList<BillingCity>> ListByCountriesAsync(
        IReadOnlyCollection<string> countryCodes, CancellationToken cancellationToken);

    /// <summary>
    /// Las primeras <paramref name="limit"/> ciudades del país por nombre normalizado, estado y código.
    /// <paramref name="normalizedSearch"/> (ya de <see cref="CityText.Normalize"/>) se busca dentro del nombre; <c>Contains</c>
    /// se traduce a <c>strpos</c>, así que un <c>%</c> o un <c>_</c> del buscador son texto y no comodines.
    /// </summary>
    Task<IReadOnlyList<BillingCity>> SearchAsync(
        string countryCode, string? normalizedSearch, int limit, CancellationToken cancellationToken);

    /// <summary>Los países distintos del catálogo con su nombre.</summary>
    Task<IReadOnlyList<BillingCountry>> ListCountriesAsync(CancellationToken cancellationToken);
}
