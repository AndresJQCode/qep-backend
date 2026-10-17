using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Los textos del historial del pedido (spec 2026-10-15 §4.6). Son hechos que lee una persona:
/// en español, y donde se dirigen a alguien, tuteando.</summary>
public static class OrderChangeSummary
{
    public static string CreatedFromQuotation(string quotationNumber) =>
        Trim($"Creado desde la cotización {quotationNumber}.");

    public static string CreatedManually() => "Creado a mano desde Pedidos.";

    public static string CreatedFromExternal(OrderSource source, string reference) =>
        Trim($"Creado desde {source} ({reference}).");

    public static string ItemAdded(string productName, decimal quantity) =>
        QuotationChangeSummary.ItemAdded(productName, quantity);

    public static string ItemQuantityChanged(string productName, decimal from, decimal to) =>
        QuotationChangeSummary.ItemQuantityChanged(productName, from, to);

    public static string ItemRemoved(string productName) => QuotationChangeSummary.ItemRemoved(productName);

    public static string RetailChanged(bool isRetail) => isRetail
        ? "Marcó el pedido como detal: ninguna línea recibe descuento."
        : "Quitó el detal: las líneas vuelven a su descuento por escala.";

    public static string GlobalScaleFloorChanged(int? floor) => QuotationChangeSummary.GlobalScaleFloorChanged(floor);

    public static string NotesChanged() => "Editó las notas.";

    /// <summary>El cambio de envío de un pedido pendiente (spec 2026-10-17 §5.4), con los textos de la
    /// cotización. <c>null</c> si nada de lo que se cuenta cambió.</summary>
    public static string? ShippingChanged(ShippingDetails before, ShippingDetails after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<string>(4);
        QuotationChangeSummary.AppendFreight(changes, before, after);
        return changes.Count == 0 ? null : Trim($"Editó {string.Join(", ", changes)}.");
    }

    public static string Approved() => "Aprobado.";

    public static string Cancelled(string reason) => Trim($"Anulado: {reason}");

    public static string Invoiced() => "Marcado como facturado.";

    public static string InvoicingReverted() => "Se revirtió la facturación.";

    public static string PaymentProofsChanged(int added, int corrected, int removed)
    {
        var parts = new List<string>(3);
        if (added > 0)
        {
            parts.Add($"{added} comprobante(s) nuevo(s)");
        }

        if (corrected > 0)
        {
            parts.Add($"{corrected} corregido(s)");
        }

        if (removed > 0)
        {
            parts.Add($"{removed} retirado(s)");
        }

        return Trim($"Comprobantes: {string.Join(", ", parts)}.");
    }

    private static string Trim(string summary) =>
        summary.Length <= OrderHistoryEntry.DetailsMaxLength
            ? summary
            : summary[..(OrderHistoryEntry.DetailsMaxLength - 1)] + "…";
}
