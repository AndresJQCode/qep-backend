namespace Modules.Hr.Application;

internal static class HrEnums
{
    /// <summary>
    /// Sólo el nombre exacto del miembro (<c>ignoreCase: false</c>): un número no es un enum, y <c>Enum.TryParse</c> lo aceptaría
    /// («1», «III» y «3» no son lo mismo) y también aceptaría un valor fuera de rango.
    /// </summary>
    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return text is not null
            && Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
            && Enum.TryParse(text, ignoreCase: false, out value);
    }
}
