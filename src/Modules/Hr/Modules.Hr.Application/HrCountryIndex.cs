namespace Modules.Hr.Application;

/// <summary>
/// El catálogo de países armado una vez por request (una sola <c>ListCountriesAsync</c>): por código o por nombre sin
/// mayúsculas, tildes ni espacios de más.
/// </summary>
public sealed class HrCountryIndex
{
    private readonly Dictionary<string, HrCountryRef> _byCode;
    private readonly Dictionary<string, HrCountryRef> _byFoldedName;

    public HrCountryIndex(IReadOnlyList<HrCountryRef> countries)
    {
        _byCode = countries.ToDictionary(country => country.Code, StringComparer.Ordinal);
        _byFoldedName = countries
            .GroupBy(country => HrText.Fold(country.Name) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Names = countries.Select(country => country.Name).ToList();
    }

    /// <summary>Los nombres, para la hoja «Valores válidos».</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>El código va en mayúsculas: «co» no es un código, es un texto que <see cref="FindByCodeOrName"/> resuelve.</summary>
    public bool IsKnown(string code) => _byCode.ContainsKey(code);

    public HrCountryRef? FindByCodeOrName(string text)
    {
        var trimmed = text.Trim();
        if (_byCode.TryGetValue(trimmed.ToUpperInvariant(), out var byCode))
        {
            return byCode;
        }

        return HrText.Fold(trimmed) is { } folded ? _byFoldedName.GetValueOrDefault(folded) : null;
    }
}
