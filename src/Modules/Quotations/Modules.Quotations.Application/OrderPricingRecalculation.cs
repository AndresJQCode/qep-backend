using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Espejo de <see cref="QuotationPricingRecalculation"/> sobre el pedido (plan 2026-10-15, P4): los
/// descuentos de grupo por escala, el piso global y el mínimo de compra se resuelven con las mismas
/// reglas que en el cotizador. Si una de las dos cambia, cambia la otra; las pruebas unitarias de
/// cada una son el candado.
/// </summary>
internal static class OrderPricingRecalculation
{
    public static async Task ApplyAsync(
        IQuotationProductPricingLookup lookup,
        IQuotationSettingsStore settingsStore,
        Guid tenantId,
        Order order,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (order.Items.Count == 0)
        {
            return;
        }

        if (order.IsRetail)
        {
            order.ApplyGroupDiscounts(
                order.Items.ToDictionary(
                    item => item.Id,
                    _ => new QuotationItemDiscount(0m, QuotationDiscountOrigin.Own)),
                occurredAt);
            return;
        }

        var products = await lookup.FindManyAsync(
            tenantId,
            order.Items.Select(item => item.ProductId).Distinct().ToArray(),
            cancellationToken);
        var scalesByProduct = order.Items
            .Select(item => item.ProductId)
            .Distinct()
            .ToDictionary(
                productId => productId,
                productId => products.TryGetValue(productId, out var product)
                    ? product.Scales
                    : []);
        var lines = order.Items
            .Select(item => new QuotationPricingLine(item.Id.Value, item.ProductId, item.Quantity))
            .ToArray();

        var discounts = Apply(order, lines, scalesByProduct, order.GlobalScaleFloor, occurredAt);
        var settings = await settingsStore.GetAsync(tenantId, cancellationToken);
        if (QuotationMinimumPurchase.IsMet(order.Items.Sum(item => item.Quantity), order.Total, order.Currency, settings))
        {
            return;
        }

        if (order.GlobalScaleFloor is not null)
        {
            discounts = Apply(order, lines, scalesByProduct, globalFloor: null, occurredAt);
            if (QuotationMinimumPurchase.IsMet(order.Items.Sum(item => item.Quantity), order.Total, order.Currency, settings))
            {
                return;
            }
        }

        order.ApplyGroupDiscounts(
            discounts.ToDictionary(
                discount => discount.Key,
                _ => new QuotationItemDiscount(0m, QuotationDiscountOrigin.Own)),
            occurredAt);
    }

    private static Dictionary<OrderItemId, QuotationItemDiscount> Apply(
        Order order,
        IReadOnlyCollection<QuotationPricingLine> lines,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByProduct,
        int? globalFloor,
        DateTimeOffset occurredAt)
    {
        var discounts = QuotationScaleGroupPricing
            .Resolve(lines, scalesByProduct, globalFloor)
            .ToDictionary(
                line => new OrderItemId(line.ItemId),
                line => new QuotationItemDiscount(line.DiscountPercentage, line.Origin));
        order.ApplyGroupDiscounts(discounts, occurredAt);
        return discounts;
    }
}
