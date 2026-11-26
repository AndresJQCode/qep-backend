namespace Modules.Quotations.Application;

/// <summary>Los ajustes del pedido por tenant (spec 2026-11-10 §6), en la misma fila que el mínimo de compra.</summary>
/// <param name="RequirePaymentProofToMarkPaid">«Marcar como pagado» exige al menos un comprobante.</param>
/// <param name="RequireFullPaymentToInvoice">Facturar exige el pago completo.</param>
/// <param name="AutoArchive">Archivar el pedido cuando queda pagado y preparado.</param>
public sealed record OrderSettings(bool RequirePaymentProofToMarkPaid, bool RequireFullPaymentToInvoice, bool AutoArchive)
{
    /// <summary>Los valores del owner: un tenant sin fila o que nunca los tocó.</summary>
    public static OrderSettings Default { get; } = new(false, true, false);
}
