using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal enum QuotationItemEditKind
{
    Added,
    QuantityChanged,
    Removed
}

/// <summary>Un cambio ya aplicado sobre la cotización, para el historial y la auditoría del
/// guardado. <paramref name="ProductName"/> es null en una baja: quitar no resuelve precio, y el
/// nombre lo busca el caso de uso con <c>IQuotationProductLookup</c>, como
/// <c>RemoveQuotationItemHandler</c>.</summary>
internal sealed record QuotationItemEdit(
    QuotationItemEditKind Kind,
    Guid ProductId,
    string? ProductName,
    decimal? PreviousQuantity,
    decimal? Quantity);

/// <summary>
/// Lleva las líneas de una cotización **editable** (Draft o Sent) a la lista completa deseada. La
/// clave es <c>productId</c>: el dominio ya prohíbe dos líneas del mismo producto
/// (<c>quotation.item.duplicate_product</c>).
///
/// Gemelo de <see cref="OrderItemEdits"/>, pero por el camino editable: usa <c>AddItem</c>,
/// <c>UpdateItemQuantity</c> y <c>RemoveItem</c>, que pasan por <c>EnsureEditable</c>, y no las
/// variantes <c>*AfterConversion</c>, que existen justamente para saltárselo.
///
/// El orden es <b>altas, cambios, bajas</b> — el mismo que <see cref="OrderItemEdits"/> y el
/// inverso de <see cref="BatchUpdateQuotationItemsHandler"/>. Acá el motivo no es
/// <c>last_item_required</c> (ese chequeo no existe en <c>RemoveItem</c>) sino la posición:
/// <c>Quotation.AddItemCore</c> numera la línea nueva con <c>_items.Count + 1</c>, así que bajar
/// primero le daría a la nueva un número que otra línea que se queda todavía ocupa. Y que los dos
/// guardados de una vez —pedido y cotización— difieran en el orden sería una trampa para el
/// próximo que lea uno creyendo entender el otro.
///
/// Lo usan el guardado (agregado rastreado) y el cálculo previo (sin rastreo): mismos métodos de
/// dominio para los dos. No escribe historial ni auditoría: eso es sólo del guardado.
/// </summary>
internal static class QuotationItemEdits
{
    public static async Task<IReadOnlyList<QuotationItemEdit>> ApplyAsync(
        Quotation quotation,
        IReadOnlyCollection<QuotationItemAddition> desired,
        IQuotationProductPricingLookup pricingLookup,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        // Foto de antes de mutar: las altas agregan líneas a quotation.Items mientras se recorre.
        var current = quotation.Items.ToDictionary(item => item.ProductId);
        var desiredProductIds = desired.Select(line => line.ProductId).ToHashSet();
        var edits = new List<QuotationItemEdit>();

        foreach (var line in desired.Where(line => !current.ContainsKey(line.ProductId)))
        {
            // Rechaza inexistente, inactivo o sin precio en la moneda, con los mismos códigos que
            // POST /quotations/{quotationId}/items.
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.ProductId, line.Quantity, quotation.Currency, cancellationToken);

            quotation.AddItem(
                QuotationItemId.New(), line.ProductId, line.Quantity,
                pricing.Pricing.UnitPrice, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new QuotationItemEdit(
                QuotationItemEditKind.Added, line.ProductId, pricing.Name, null, line.Quantity));
        }

        foreach (var line in desired)
        {
            if (!current.TryGetValue(line.ProductId, out var item) || item.Quantity == line.Quantity)
            {
                continue;
            }

            // Antes de que UpdateItemQuantity la pise: el historial dice de cuánto a cuánto. La
            // escala y el impuesto se vuelven a resolver, igual que en UpdateQuotationItemHandler.
            var previousQuantity = item.Quantity;
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.ProductId, line.Quantity, quotation.Currency, cancellationToken);

            quotation.UpdateItemQuantity(
                item.Id, line.Quantity, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new QuotationItemEdit(
                QuotationItemEditKind.QuantityChanged, line.ProductId, pricing.Name, previousQuantity, line.Quantity));
        }

        foreach (var item in current.Values.Where(item => !desiredProductIds.Contains(item.ProductId)).ToArray())
        {
            quotation.RemoveItem(item.Id, updatedBy, occurredAt);
            edits.Add(new QuotationItemEdit(
                QuotationItemEditKind.Removed, item.ProductId, null, item.Quantity, null));
        }

        return edits;
    }
}
