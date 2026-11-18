namespace Modules.Wholesale.Infrastructure.Persistence;

/// <summary>Patrones para <c>ILIKE</c> sobre un término escrito por el usuario (copia de <c>LikeSearch</c> de Loans).</summary>
internal static class LikeSearch
{
    public const string EscapeCharacter = "\\";

    // La barra va primero: escaparla después convertiría en literal la barra que acaban de agregar los otros dos reemplazos.
    private static string Escape(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    // null para un término vacío o ausente: "%%" coincidiría con todo, que no es lo mismo que no filtrar.
    public static string? ContainsPattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{Escape(trimmed)}%";
    }
}
