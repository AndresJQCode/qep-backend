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

    /// <summary>Los códigos con que Siigo avisa que un servicio suyo no está disponible (catálogo «Manejo de errores» de su documentación).
    /// Llegan en un <b>400</b>, no en un 5xx, pero no dicen nada del dato enviado: es una caída de Siigo y se reintenta en unos minutos.
    /// <c>unhandled_error</c> no entra: es un fallo sin clasificar y puede ser del dato.</summary>
    private static readonly HashSet<string> ServiceCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "documents_service",
        "payment_types_service",
        "general_service",
        "service_unavailable",
        "request_timeout",
    };

    /// <summary>Si <b>todos</b> los errores son de servicio de Siigo. Uno solo de otro tipo (un dato rechazado) manda: el tenant tiene algo
    /// que corregir y no se le dice que espere.</summary>
    public static bool IsServiceUnavailable(IReadOnlyList<SiigoError> errors) =>
        errors.Count > 0 && errors.All(error => ServiceCodes.Contains(error.Code));

    /// <summary>
    /// Los códigos con que Siigo dice sin ambigüedad que una factura no existe: sólo un 404 con <b>todos</b> sus errores en esta lista
    /// cuenta como «borrada» (spec 2026-11-25, anulación §2). <b>Nace vacía:</b> qué contesta Siigo con una factura borrada no está
    /// verificado (spec, «Verificación pendiente»), y un 404 pelado también es una ruta mal armada o una <c>BaseUrl</c> equivocada. Se
    /// llena sólo con la respuesta real de Siigo, que queda en <c>siigo.call_failures</c> (operación <c>InvoiceRead</c>).
    /// </summary>
    public static IReadOnlySet<string> InvoiceNotFoundCodes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>De a cuántos productos pide Siigo cada página del auto-vínculo.</summary>
    public const int ProductsPageSize = 100;

    /// <summary>El total que Siigo esperaba: el <b>último</b> número del mensaje, con cultura invariante (punto decimal). <c>null</c> si
    /// no hay ninguno o no se puede leer. Se muestra junto al de QEP y sólo ajusta los pagos si la diferencia es de redondeo.</summary>
    public static decimal? ParseExpectedPaymentsTotal(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var matches = NumberPattern().Matches(message);
        return matches.Count > 0
            && decimal.TryParse(Canonical(matches[^1].Value), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    // El número completo, con miles y decimales: «288,490.50» es 288490.50 y no 490.50. Con los dos separadores, el último es el decimal;
    // con sólo comas en grupos de tres son miles («1,234,567»), y con varios puntos también; una sola coma o un solo punto es decimal.
    private static string Canonical(string number)
    {
        var lastComma = number.LastIndexOf(',');
        var lastDot = number.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            var decimalSeparator = lastComma > lastDot ? ',' : '.';
            var thousands = decimalSeparator == ',' ? '.' : ',';
            return number.Replace(thousands.ToString(), string.Empty, StringComparison.Ordinal).Replace(decimalSeparator, '.');
        }

        if (lastComma >= 0)
        {
            return GroupedThousands(number, ',') ? number.Replace(",", string.Empty, StringComparison.Ordinal) : number.Replace(',', '.');
        }

        return lastDot >= 0 && number.IndexOf('.', StringComparison.Ordinal) != lastDot && GroupedThousands(number, '.')
            ? number.Replace(".", string.Empty, StringComparison.Ordinal)
            : number;
    }

    private static bool GroupedThousands(string number, char separator)
    {
        var parts = number.Split(separator);
        return parts.Length > 1 && parts[0].Length is >= 1 and <= 3 && parts.Skip(1).All(part => part.Length == 3);
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberPattern();
}
