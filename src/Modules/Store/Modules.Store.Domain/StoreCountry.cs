namespace Modules.Store.Domain;

/// <summary>
/// País ISO-3166-1 alpha-2. El backend no tiene catálogo de países: que sea «uno real» lo decide el
/// predicado que llega de fuera, para que el dominio siga puro.
/// </summary>
public static class StoreCountry
{
    public const string Colombia = "CO";

    public static bool IsShapeValid(string? code) =>
        code is { Length: 2 } && code.All(char.IsAsciiLetter);

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    public static bool IsColombia(string country) =>
        string.Equals(country, Colombia, StringComparison.OrdinalIgnoreCase);
}
