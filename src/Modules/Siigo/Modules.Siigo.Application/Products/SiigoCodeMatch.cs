namespace Modules.Siigo.Application.Products;

/// <summary>
/// Spec 2026-11-18 §5.3, paso 6: el criterio con el que «Vincular por código» empareja una variante de QEP con un producto de Siigo: el
/// mismo código <b>sin distinguir mayúsculas y sin espacios</b>. Un código vacío o ausente no casa con nada (dos productos sin código
/// no son el mismo producto).
/// </summary>
public static class SiigoCodeMatch
{
    /// <summary>El código sin ningún espacio y en mayúsculas, o <c>null</c> si queda vacío.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = string.Concat(code.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}
