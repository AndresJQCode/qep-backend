namespace Modules.Store.Domain;

/// <summary>
/// Recorte y límites de los textos que escribe el visitante. La forma ya la validó la capa de
/// aplicación; esto es el cinturón del dominio, para que un bloque congelado nunca exceda las
/// columnas ni llegue con espacios sobrantes.
/// </summary>
internal static class StoreText
{
    public static string Required(string? value, int maxLength, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            throw new StoreDomainException(
                StoreErrorCodes.ValidationFailed,
                $"{field} es obligatorio y admite hasta {maxLength} caracteres.");
        }

        return trimmed;
    }

    public static string? Optional(string? value, int maxLength, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new StoreDomainException(
                StoreErrorCodes.ValidationFailed,
                $"{field} admite hasta {maxLength} caracteres.");
        }

        return trimmed;
    }

    /// <summary>Recorta y convierte vacío en null, sin límite: para ajustes que valida la capa de aplicación.</summary>
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
