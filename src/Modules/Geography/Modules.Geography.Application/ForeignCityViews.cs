using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>De las filas de la bandeja del exterior y del catálogo a los DTO de la consola. Lo comparten la lista, el detalle y
/// las transiciones.</summary>
internal static class ForeignCityViews
{
    public static BillingCountryRefDto Country(string code, string name) => new(code, name);

    public static BillingCityRefDto City(BillingCity city) =>
        new(city.Id.Value, city.CityName, city.StateName, city.CityCode);

    /// <summary>El nombre del país sale de cualquiera de sus ciudades; si no hubiera ninguna, el código es lo único que se
    /// puede mostrar. <paramref name="cities"/> son las del país de la fila.</summary>
    public static BillingCountryRefDto CountryOf(string countryCode, IReadOnlyList<BillingCity> cities) =>
        new(countryCode, cities.Count == 0 ? countryCode : cities[0].CountryName);

    /// <summary>Hasta <see cref="CitySuggestions.MaxSuggestions"/>, en el orden de <see cref="CitySuggestions.Rank(string, IEnumerable{BillingCity})"/>.
    /// <paramref name="cities"/> son las del país de la fila.</summary>
    public static IReadOnlyList<BillingCitySuggestionDto> Suggestions(
        UnresolvedForeignCity row, IReadOnlyList<BillingCity> cities)
    {
        var byId = cities.ToDictionary(city => city.Id);
        return CitySuggestions
            .Rank(row.NormalizedText, cities)
            .Select(suggestion =>
            {
                var city = byId[suggestion.BillingCityId];
                return new BillingCitySuggestionDto(
                    city.Id.Value, city.CityName, city.StateName, city.CityCode, suggestion.Similarity);
            })
            .ToList();
    }

    public static UnresolvedForeignCityListItemDto ListItem(UnresolvedForeignCityRow row, IReadOnlyList<BillingCity> countryCities)
    {
        var city = row.City;
        var suggestions = Suggestions(city, countryCities);
        return new UnresolvedForeignCityListItemDto(
            city.Id.Value,
            city.CityText,
            city.NormalizedText,
            CountryOf(city.CountryCode, countryCities),
            city.Status.ToString(),
            row.OrderCount,
            row.TenantCount,
            city.FirstSeenAt,
            city.LastSeenAt,
            suggestions.Count == 0 ? null : suggestions[0],
            city.Version);
    }

    /// <summary>La fila como la pinta la tabla, desde sus ocurrencias: lo que devuelven descartar y restaurar.</summary>
    public static UnresolvedForeignCityRow Row(
        UnresolvedForeignCity city, IReadOnlyList<UnresolvedForeignCityOccurrence> occurrences) =>
        new(city, occurrences.Count, occurrences.Select(occurrence => occurrence.TenantId).Distinct().Count());

    public static ForeignCitySynonymDto Synonym(ForeignCitySynonym synonym, BillingCity city, string? createdByEmail) =>
        new(
            synonym.Id.Value,
            synonym.DisplayText,
            synonym.NormalizedText,
            Country(synonym.CountryCode, city.CountryName),
            City(city),
            new CitySynonymActorDto(synonym.CreatedBy, createdByEmail),
            synonym.CreatedAt,
            synonym.UpdatedAt,
            synonym.Version);
}
