using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>De la fila de la bandeja (y las ciudades de su departamento) a los DTO de la consola. Lo comparten la lista, el
/// detalle y las transiciones (descartar y restaurar devuelven la fila como la pinta la tabla).</summary>
internal static class UnresolvedCityViews
{
    public static DepartmentRefDto Department(Department department) =>
        new(department.Id.Value, department.Name, department.DivipolaCode);

    public static CityRefDto City(City city) => new(city.Id.Value, city.Name, city.DivipolaCode);

    /// <summary>Un tenant no se borra, así que siempre debería tener nombre; si faltara, el id es lo único que se puede
    /// mostrar.</summary>
    public static string TenantName(IReadOnlyDictionary<Guid, string> names, Guid tenantId) =>
        names.TryGetValue(tenantId, out var name) ? name : tenantId.ToString();

    /// <summary>Hasta <see cref="CitySuggestions.MaxSuggestions"/>, en el orden de <see cref="CitySuggestions.Rank"/>.
    /// <paramref name="cities"/> son las del departamento de la fila.</summary>
    public static IReadOnlyList<CitySuggestionDto> Suggestions(UnresolvedCity row, IReadOnlyList<City> cities)
    {
        var byId = cities.ToDictionary(city => city.Id);
        return CitySuggestions
            .Rank(row.NormalizedText, cities.Select(city => new CitySuggestionCandidate(city.Id, city.Name, city.DivipolaName)))
            .Select(suggestion =>
            {
                var city = byId[suggestion.CityId];
                return new CitySuggestionDto(city.Id.Value, city.Name, city.DivipolaCode, city.DivipolaName, suggestion.Similarity);
            })
            .ToList();
    }

    public static UnresolvedCityListItemDto ListItem(
        UnresolvedCityRow row, Department department, IReadOnlyList<City> departmentCities)
    {
        var city = row.City;
        var best = Suggestions(city, departmentCities);
        return new UnresolvedCityListItemDto(
            city.Id.Value,
            city.CityText,
            city.NormalizedText,
            Department(department),
            city.Status.ToString(),
            row.OrderCount,
            row.TenantCount,
            city.FirstSeenAt,
            city.LastSeenAt,
            best.Count == 0 ? null : best[0],
            city.Version);
    }
}
