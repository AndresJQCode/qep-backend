using System.Globalization;

namespace Modules.Support.Domain;

/// <summary>El radicado visible: <c>SOP-</c> más seis dígitos como mínimo (<c>SOP-000127</c>). En la base se guarda el número.</summary>
public static class SupportTicketNumber
{
    public const string Prefix = "SOP-";

    public static string Format(long number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        return Prefix + number.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Acepta el radicado con o sin prefijo y sin distinguir mayúsculas (<c>SOP-000127</c>, <c>sop-127</c>, <c>127</c>):
    /// así lo dicta un solicitante por teléfono.</summary>
    public static bool TryParse(string? text, out long number)
    {
        number = 0;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..];
        }

        if (value.Length == 0 || !value.All(char.IsAsciiDigit) ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return false;
        }

        number = parsed;
        return true;
    }
}
