namespace Modules.Shopify.Domain;

/// <summary>
/// Spec 2026-11-14: los estados de pago del pedido de QEP con los que la tienda admite que se aplique un cambio de Shopify.
/// Shopify no ve Quotations, así que son una copia de los nombres de <c>Quotations.OrderPaymentStatus</c>; la amarra
/// <c>ShopifyPaymentStatusesParityTests</c> (Bootstrapper.UnitTests), que ve los dos módulos.
/// </summary>
public static class ShopifyPaymentStatuses
{
    /// <summary>Copia de los nombres de <c>Quotations.OrderPaymentStatus</c>, en su orden. También es el valor por defecto de
    /// la tienda: una tienda nueva admite cambios con cualquier estado de pago.</summary>
    public static IReadOnlyList<string> All { get; } =
        ["FullPaymentReceived", "PartialPaymentReceived", "PaymentPending", "PendingVerification"];

    /// <summary>Ordinal: <c>paymentpending</c> no es un estado.</summary>
    public static bool IsKnown(string value) => All.Contains(value, StringComparer.Ordinal);
}
