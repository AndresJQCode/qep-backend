namespace Modules.Hr.Application;

public enum HrCityOutcome
{
    Found,
    NotFound,
    Ambiguous,
}

public readonly record struct HrCityMatch(HrCityOutcome Outcome, Guid? CityId);

/// <summary>
/// Las ciudades de Geografía armadas una vez por request, para resolver la columna <c>CIUDAD</c> por nombre: sin mayúsculas ni
/// tildes, por el nombre o por el nombre DIVIPOLA, y con «ciudad - departamento» para desambiguar.
/// </summary>
public sealed class HrCityIndex
{
    private const string Separator = " - ";

    private readonly List<(HrCityRef City, string Name, string DivipolaName, string Department)> _cities;

    public HrCityIndex(IReadOnlyList<HrCityRef> cities) =>
        _cities = cities
            .Select(city => (
                city,
                HrText.Fold(city.Name) ?? string.Empty,
                HrText.Fold(city.DivipolaName) ?? string.Empty,
                HrText.Fold(city.DepartmentName) ?? string.Empty))
            .ToList();

    public HrCityMatch Match(string text)
    {
        if (HrText.Fold(text) is not { } folded)
        {
            return new HrCityMatch(HrCityOutcome.NotFound, null);
        }

        var candidates = _cities.Where(city => city.Name == folded || city.DivipolaName == folded).ToList();
        if (candidates.Count == 0 && folded.LastIndexOf(Separator, StringComparison.Ordinal) is var cut and > 0)
        {
            var cityPart = folded[..cut].Trim();
            var departmentPart = folded[(cut + Separator.Length)..].Trim();
            candidates = _cities
                .Where(city => (city.Name == cityPart || city.DivipolaName == cityPart) && city.Department == departmentPart)
                .ToList();
        }

        var ids = candidates.Select(city => city.City.CityId).Distinct().ToList();
        return ids.Count switch
        {
            0 => new HrCityMatch(HrCityOutcome.NotFound, null),
            1 => new HrCityMatch(HrCityOutcome.Found, ids[0]),
            _ => new HrCityMatch(HrCityOutcome.Ambiguous, null),
        };
    }
}
