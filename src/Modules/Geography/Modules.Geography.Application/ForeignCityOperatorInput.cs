namespace Modules.Geography.Application;

/// <summary>Límites y formas de entrada de la consola del exterior. Los de paginación y búsqueda son los de
/// <see cref="CityOperatorInput"/>.</summary>
public static class ForeignCityOperatorInput
{
    /// <summary>Tope de ciudades que responde el buscador del catálogo: el selector del modal no pagina.</summary>
    public const int CatalogLimit = 50;

    /// <summary>Dos letras ASCII. Se compara en mayúscula: <c>us</c> y <c>US</c> son el mismo país.</summary>
    public static bool IsCountryCode(string value) =>
        value is { Length: 2 } && value.All(char.IsAsciiLetter);

    /// <summary>Sólo se llama después de validar.</summary>
    public static string? NormalizeCountry(string? value) => value?.ToUpperInvariant();
}
