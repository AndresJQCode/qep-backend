using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal enum OrderItemEditKind
{
    Added,
    QuantityChanged,
    Removed
}

/// <summary>Un cambio ya aplicado sobre la cotización, para el historial y la auditoría del
/// guardado. <paramref name="ProductName"/> es null en una baja: quitar no resuelve precio, y el
/// nombre lo busca el caso de uso con <c>IQuotationProductLookup</c>, como
/// <c>RemoveQuotationItemHandler</c>.</summary>
internal sealed record OrderItemEdit(
    OrderItemEditKind Kind,
    Guid ProductId,
    string? ProductName,
    decimal? PreviousQuantity,
    decimal? Quantity);

/// <summary>
/// Lleva las líneas de la cotización de un pedido pendiente a la lista **completa** deseada (spec
/// 2026-09-17, decisión 2). La clave es <c>productId</c>: el dominio ya prohíbe dos líneas del mismo
/// producto (<c>quotation.item.duplicate_product</c>).
///
/// El orden es **altas, cambios, bajas**, el inverso de <see cref="BatchUpdateQuotationItemsHandler"/>
/// a propósito: en un reemplazo total, bajar primero dispararía
/// <c>quotation.item.last_item_required</c> a mitad del comando. Altas y bajas nunca comparten
/// <c>productId</c>, así que no se pisan.
///
/// Lo usan el guardado (agregado rastreado) y el cálculo previo (sin rastreo): mismos métodos de
/// dominio para los dos (decisión 4). No escribe historial ni auditoría: eso es sólo del guardado.
/// </summary>
internal static class OrderItemEdits
{
    public static async Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Quotation quotation,
        IReadOnlyCollection<OrderItemAddition> desired,
        IQuotationProductPricingLookup pricingLookup,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        // Foto de antes de mutar: las altas agregan líneas a quotation.Items mientras se recorre.
        var current = quotation.Items.ToDictionary(item => item.ProductId);
        var desiredProductIds = desired.Select(line => line.ProductId).ToHashSet();
        var edits = new List<OrderItemEdit>();

        foreach (var line in desired.Where(line => !current.ContainsKey(line.ProductId)))
        {
            // Rechaza inexistente, inactivo o sin precio en la moneda, con los mismos códigos que
            // POST /orders/{orderId}/items.
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.ProductId, line.Quantity, quotation.Currency, cancellationToken);

            quotation.AddItemAfterConversion(
                QuotationItemId.New(), line.ProductId, line.Quantity,
                pricing.Pricing.UnitPrice, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Added, line.ProductId, pricing.Name, null, line.Quantity));
        }

        foreach (var line in desired)
        {
            if (!current.TryGetValue(line.ProductId, out var item) || item.Quantity == line.Quantity)
            {
                continue;
            }

            // Antes de que UpdateItemQuantityAfterConversion la pise: el historial dice de cuánto a
            // cuánto. La escala y el impuesto se vuelven a resolver, igual que en
            // UpdateQuotationItemHandler.
            var previousQuantity = item.Quantity;
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.ProductId, line.Quantity, quotation.Currency, cancellationToken);

            quotation.UpdateItemQuantityAfterConversion(
                item.Id, line.Quantity, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(
                OrderItemEditKind.QuantityChanged, line.ProductId, pricing.Name, previousQuantity, line.Quantity));
        }

        foreach (var item in current.Values.Where(item => !desiredProductIds.Contains(item.ProductId)).ToArray())
        {
            quotation.RemoveItemAfterConversion(item.Id, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Removed, item.ProductId, null, item.Quantity, null));
        }

        return edits;
    }
}
