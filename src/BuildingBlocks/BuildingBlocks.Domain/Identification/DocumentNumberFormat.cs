namespace BuildingBlocks.Domain.Identification;

/// <summary>
/// Regla compartida del número de documento: los tipos numéricos colombianos (NIT, CC y CE) solo admiten
/// dígitos, con los separadores con que la gente los escribe (punto, guion y espacio). Los demás tipos
/// (pasaporte, NIF, NIE, TAX_ID) son alfanuméricos y no se tocan. Los códigos son los del wire.
/// </summary>
public static class DocumentNumberFormat
{
    /// <summary>Los tipos cuyo número solo puede tener dígitos y separadores.</summary>
    public static IReadOnlyList<string> NumericTypes { get; } = ["NIT", "CC", "CE"];

    public const string NumericTypesMessage = "El número de documento solo puede tener números para NIT, CC y CE.";

    /// <summary>Si el tipo exige un número con solo dígitos. Recorta y no distingue mayúsculas.</summary>
    public static bool RequiresDigits(string? type) =>
        type is not null
        && NumericTypes.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Si el número solo tiene dígitos, punto, guion y (opcionalmente) espacio, con al menos un dígito.
    /// Un número vacío no es numérico: que falte lo dice otra regla.
    /// </summary>
    public static bool IsNumeric(string? number, bool allowSpaces = true)
    {
        var trimmed = number?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var hasDigit = false;
        foreach (var character in trimmed)
        {
            if (char.IsAsciiDigit(character))
            {
                hasDigit = true;
            }
            else if (character is not ('.' or '-') && !(allowSpaces && character == ' '))
            {
                return false;
            }
        }

        return hasDigit;
    }

    /// <summary>Los tipos que no son numéricos pasan siempre; los numéricos exigen <see cref="IsNumeric"/>.</summary>
    public static bool IsValidFor(string? type, string? number) =>
        !RequiresDigits(type) || IsNumeric(number);
}
