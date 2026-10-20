using System.Text.RegularExpressions;

namespace Modules.Shopify.Application;

/// <summary>
/// Decisión del owner del 2026-10-20: <c>last_error</c> del espejo es texto libre para soporte y logs, y la bandeja pinta
/// <see cref="ShopifyOrderRowDto.LastErrorCodes"/>, que sale de acá. El backend es dueño del formato de <c>last_error</c>, así que
/// lo parte él y no la pantalla. Lo que escriben los cuatro orígenes:
/// <list type="bullet">
/// <item><see cref="ShopifyOrderChanges"/>: los códigos que Quotations rechazó en la anulación y el pago del mismo aviso, unidos
/// por <c>"; "</c> (<c>order.order.not_pending</c>, <c>order.order.payment_has_proofs</c>, <c>order.order.not_found</c>, …), o el
/// nombre del enum (<c>Ignored</c>, <c>Unchanged</c>) si el puerto no dio código.</item>
/// <item><see cref="ShopifyOrderImporter"/>: el código del pago rechazado justo después del alta.</item>
/// <item>El reintento manual y la conciliación: <c>invalid_payload: campo: mensaje</c> (mensaje en inglés, del parser).</item>
/// </list>
/// Regla: cada parte recortada que empieza con <c>invalid_payload</c> es <see cref="InvalidPayload"/>; una que ya tiene forma de
/// código (<c>a.b</c>, minúsculas, dígitos y guiones bajos) viaja tal cual; cualquier otra cosa es <see cref="SyncFailed"/>. En el
/// orden en que aparecen y sin repetir.
/// </summary>
public static partial class ShopifyLastErrorCodes
{
    /// <summary>El snapshot de Shopify no se pudo leer (cabecera rota, payload ilegible del espejo).</summary>
    public const string InvalidPayload = "shopify.order.invalid_payload";

    /// <summary>Cualquier <c>last_error</c> que no es un código: el frontend lo traduce con un texto genérico.</summary>
    public const string SyncFailed = "shopify.order.sync_failed";

    private const string Separator = "; ";

    public static IReadOnlyList<string> Normalize(string? lastError)
    {
        if (string.IsNullOrWhiteSpace(lastError))
        {
            return [];
        }

        var codes = new List<string>();
        foreach (var part in lastError.Split(Separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var code = part.StartsWith(ShopifyDeliveryResult.InvalidPayloadPrefix, StringComparison.Ordinal) ? InvalidPayload
                : CodeShape().IsMatch(part) ? part
                : SyncFailed;
            if (!codes.Contains(code, StringComparer.Ordinal))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    [GeneratedRegex(@"^[a-z0-9_]+(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();
}
