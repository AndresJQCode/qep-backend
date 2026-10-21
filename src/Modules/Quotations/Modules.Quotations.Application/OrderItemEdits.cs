using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal enum OrderItemEditKind
{
    Added,
    QuantityChanged,
    Removed
}

internal sealed record OrderItemEdit(
    OrderItemEditKind Kind,
    Guid VariantId,
    string? ProductName,
    decimal? PreviousQuantity,
    decimal? Quantity);

/// <summary>
/// Lleva las líneas del pedido a la lista deseada (spec 2026-09-17 y spec 2026-10-15 §4.5): agrega
/// las que faltan, reprecia las que cambian de cantidad y quita las que sobran, sobre el pedido y
/// no sobre la cotización. Agrega antes de quitar para que la regla de «última línea» no se dispare
/// al reemplazar todos los productos. Una línea que no cambia no se toca ni se reprecia: si su
/// variante ya no existe en el catálogo, conserva su foto. La clave es <c>variantId</c> (spec
/// 2026-10-20 §4): dos tallas del mismo producto son dos líneas.
/// </summary>
internal static class OrderItemEdits
{
    public static async Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Order order,
        IReadOnlyCollection<OrderItemAddition> desired,
        IQuotationProductPricingLookup pricingLookup,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        bool isRetail,
        CancellationToken cancellationToken)
    {
        var current = order.Items.ToDictionary(item => item.VariantId);
        var desiredVariantIds = desired.Select(line => line.VariantId).ToHashSet();
        var edits = new List<OrderItemEdit>();

        foreach (var line in desired.Where(line => !current.ContainsKey(line.VariantId)))
        {
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.VariantId, line.Quantity, order.Currency, isRetail, cancellationToken);
            order.AddItem(
                OrderItemId.New(), line.VariantId, pricing.ProductId, line.Quantity,
                pricing.Pricing.UnitPrice, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Added, line.VariantId, pricing.Name, null, line.Quantity));
        }

        foreach (var line in desired)
        {
            if (!current.TryGetValue(line.VariantId, out var item) || item.Quantity == line.Quantity)
            {
                continue;
            }

            var previousQuantity = item.Quantity;
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup, tenantId, line.VariantId, line.Quantity, order.Currency, isRetail, cancellationToken);
            order.UpdateItemQuantity(
                item.Id, line.Quantity, pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(
                OrderItemEditKind.QuantityChanged, line.VariantId, pricing.Name, previousQuantity, line.Quantity));
        }

        foreach (var item in current.Values.Where(item => !desiredVariantIds.Contains(item.VariantId)).ToArray())
        {
            order.RemoveItem(item.Id, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Removed, item.VariantId, null, item.Quantity, null));
        }

        return edits;
    }
}
