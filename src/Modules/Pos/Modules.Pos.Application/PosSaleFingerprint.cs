using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modules.Pos.Application;

/// <summary>
/// Huella del request (spec, «Crear venta — idempotencia»): SHA-256 en hex minúscula de un JSON
/// canónico con posiciones fijas, [cashSessionId, [[productId, quantity, discountPercentage,
/// expectedUnitPrice, expectedTaxPercentage], …], [[method, amount, tendered, reference], …]]. Los
/// decimales van como texto 0.## (la escala ya está validada: 2, 2.0 y 2.00 son lo mismo) y lo
/// ausente como null de JSON, así null no se confunde con "" y un separador dentro de una cadena no
/// imita otro reparto de campos. Se eligió la huella y no "total + líneas" porque dos carritos
/// distintos pueden dar el mismo total.
/// </summary>
internal static class PosSaleFingerprint
{
    public static string Compute(CreatePosSaleCommand command)
    {
        object?[] canonical =
        [
            command.CashSessionId.ToString("D"),
            command.Lines
                .Select(line => new object?[]
                {
                    line.ProductId.ToString("D"),
                    Format(line.Quantity),
                    Format(line.DiscountPercentage),
                    Format(line.ExpectedUnitPrice),
                    line.ExpectedTaxPercentage.ToString(CultureInfo.InvariantCulture),
                })
                .ToArray(),
            command.Payments
                .Select(payment => new object?[]
                {
                    payment.Method,
                    Format(payment.Amount),
                    Format(payment.Tendered),
                    payment.Reference,
                })
                .ToArray(),
        ];

        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string? Format(decimal? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture);
}
