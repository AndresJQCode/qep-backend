namespace Modules.Geography.Domain;

public sealed record CitySuggestionCandidate(CityId CityId, string Name, string DivipolaName);

public sealed record CitySuggestion(CityId CityId, int Similarity);

/// <summary>Una ciudad de la lista de Siigo que se le sugiere al operador para un texto del exterior.</summary>
public sealed record BillingCitySuggestion(BillingCityId BillingCityId, int Similarity);

/// <summary>
/// Las ciudades que se le sugieren al operador para un texto que no resolvió: las tres más parecidas
/// por distancia de edición, con un piso de similitud.
/// </summary>
public static class CitySuggestions
{
    public const int MaxSuggestions = 3;

    /// <summary>Similitud mínima, inclusive, para que una ciudad entre en la lista.</summary>
    public const int MinimumSimilarity = 50;

    /// <summary>
    /// Levenshtein normalizado sobre dos textos ya normalizados:
    /// <c>round(100 * (max(len) - d) / max(len), AwayFromZero)</c>. Dos vacíos dan 0.
    /// </summary>
    public static int Score(string normalizedText, string normalizedName)
    {
        var longest = Math.Max(normalizedText.Length, normalizedName.Length);
        if (longest == 0)
        {
            return 0;
        }

        var distance = EditDistance(normalizedText, normalizedName);
        return (int)Math.Round(100m * (longest - distance) / longest, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Por ciudad, el mayor <see cref="Score"/> contra el nombre que se muestra y el oficial de DIVIPOLA;
    /// deja las de al menos <see cref="MinimumSimilarity"/>, ordena por similitud descendente y luego por
    /// nombre ordinal, y toma <see cref="MaxSuggestions"/>.
    /// </summary>
    public static IReadOnlyList<CitySuggestion> Rank(string normalizedText, IEnumerable<CitySuggestionCandidate> cities)
    {
        return cities
            .Select(city => new
            {
                city.Name,
                Suggestion = new CitySuggestion(
                    city.CityId,
                    Math.Max(
                        Score(normalizedText, CityText.Normalize(city.Name)),
                        Score(normalizedText, CityText.Normalize(city.DivipolaName)))),
            })
            .Where(item => item.Suggestion.Similarity >= MinimumSimilarity)
            .OrderByDescending(item => item.Suggestion.Similarity)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(item => item.Suggestion)
            .ToList();
    }

    /// <summary>
    /// Lo mismo contra el catálogo de Siigo de un país: el <see cref="Score"/> contra el <c>NormalizedName</c>, el piso
    /// <see cref="MinimumSimilarity"/>, y por desempate el nombre, el estado y el código ordinales (el catálogo repite
    /// nombres dentro de un país, así que el orden tiene que ser total). Colombia no pasa por acá.
    /// </summary>
    public static IReadOnlyList<BillingCitySuggestion> Rank(string normalizedText, IEnumerable<BillingCity> cities)
    {
        return cities
            .Select(city => new { City = city, Similarity = Score(normalizedText, city.NormalizedName) })
            .Where(item => item.Similarity >= MinimumSimilarity)
            .OrderByDescending(item => item.Similarity)
            .ThenBy(item => item.City.CityName, StringComparer.Ordinal)
            .ThenBy(item => item.City.StateName, StringComparer.Ordinal)
            .ThenBy(item => item.City.CityCode, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(item => new BillingCitySuggestion(item.City.Id, item.Similarity))
            .ToList();
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
