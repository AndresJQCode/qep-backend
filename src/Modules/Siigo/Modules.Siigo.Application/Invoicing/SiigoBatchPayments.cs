using System.Text.Json;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>Las formas de pago elegidas, tal como el ítem de la tanda las guarda (<c>payments_json</c>) hasta que el worker factura el
/// pedido. Una lista vacía es no haber elegido: se guarda <c>null</c> y se usa la propuesta de la cuenta de cobro.</summary>
public static class SiigoBatchPayments
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Serialize(IReadOnlyList<SiigoPaymentChoice>? payments) =>
        payments is { Count: > 0 } ? JsonSerializer.Serialize(payments, Options) : null;

    public static IReadOnlyList<SiigoPaymentChoice>? Deserialize(string? json) =>
        !string.IsNullOrWhiteSpace(json) && JsonSerializer.Deserialize<List<SiigoPaymentChoice>>(json, Options) is { Count: > 0 } payments
            ? payments
            : null;
}
