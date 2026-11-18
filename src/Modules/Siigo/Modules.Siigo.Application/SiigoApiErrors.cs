using System.Globalization;
using System.Text.RegularExpressions;

namespace Modules.Siigo.Application;

/// <summary>Los códigos de error de la API de Siigo que el módulo reconoce, y la lectura del total esperado de
/// <c>invalid_total_payments</c> (spec 2026-11-18 §3 y §6.4).</summary>
public static partial class SiigoApiErrors
{
    /// <summary>Las credenciales no sirvieron: <c>/auth</c> las rechazó, o un 401 se repitió después de renovar el token.</summary>
    public const string CredentialsRejected = "credentials_rejected";

    /// <summary>Siigo rechazó la factura porque los pagos no suman lo que él calculó (ítems, IVA y retenciones). El mensaje termina con
    /// el total que esperaba.</summary>
    public const string InvalidTotalPayments = "invalid_total_payments";

    /// <summary>De a cuántos productos pide Siigo cada página del auto-vínculo.</summary>
    public const int ProductsPageSize = 100;

    /// <summary>El total que Siigo esperaba: el <b>último</b> número del mensaje, con cultura invariante (punto decimal). <c>null</c> si
    /// no hay ninguno o no se puede leer. No se usa para ajustar los pagos: sólo se muestra junto al de QEP (spec §6.4).</summary>
    public static decimal? ParseExpectedPaymentsTotal(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var matches = NumberPattern().Matches(message);
        return matches.Count > 0
            && decimal.TryParse(matches[^1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
