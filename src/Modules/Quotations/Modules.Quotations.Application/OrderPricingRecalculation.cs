using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

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
        ITenantPriceRounding priceRounding,
        Guid tenantId,
        Order order,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        // Primer paso, antes de cualquier salida temprana (spec 2026-10-27 §4). Un pedido de una integración
        // (Shopify y las que vengan) se precia siempre con el valor por defecto de su moneda, aun editado:
        // decisión del owner. El manual y el que nació de una cotización toman la regla vigente del tenant.
        var rounding = order.Source is OrderSource.Quotation or OrderSource.Manual
            ? await priceRounding.GetAsync(tenantId, order.Currency, cancellationToken)
            : PriceRoundingDefaults.For(order.Currency);
        order.UsePriceRounding(rounding, occurredAt);

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
            order.Items.Select(item => item.VariantId).Distinct().ToArray(),
            cancellationToken);
        // Por variante, igual que el cotizador (spec 2026-10-20 §4).
        // A diferencia del cotizador, una línea cuya variante el catálogo ya no devuelve queda fuera
        // de la pasada: el pedido ya está vendido y, sin escalas contra las que recalcular, conserva el
        // descuento con el que se vendió. Darle escalas vacías lo bajaría a 0 % al editar otra línea.
        var scalesByVariant = products.ToDictionary(entry => entry.Key, entry => entry.Value.Scales);
        var lines = order.Items
            .Where(item => scalesByVariant.ContainsKey(item.VariantId))
            .Select(item => new QuotationPricingLine(item.Id.Value, item.VariantId, item.Quantity))
            .ToArray();

        var discounts = Apply(order, lines, scalesByVariant, order.GlobalScaleFloor, occurredAt);
        var settings = await settingsStore.GetAsync(tenantId, cancellationToken);
        if (QuotationMinimumPurchase.IsMet(order.Items.Sum(item => item.Quantity), order.ProductsTotal, order.Currency, settings))
        {
            return;
        }

        if (order.GlobalScaleFloor is not null)
        {
            discounts = Apply(order, lines, scalesByVariant, globalFloor: null, occurredAt);
            if (QuotationMinimumPurchase.IsMet(order.Items.Sum(item => item.Quantity), order.ProductsTotal, order.Currency, settings))
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
        IReadOnlyDictionary<Guid, IReadOnlyCollection<QuotationPriceScaleRef>> scalesByVariant,
        int? globalFloor,
        DateTimeOffset occurredAt)
    {
        var discounts = QuotationScaleGroupPricing
            .Resolve(lines, scalesByVariant, globalFloor)
            .ToDictionary(
                line => new OrderItemId(line.ItemId),
                line => new QuotationItemDiscount(line.DiscountPercentage, line.Origin));
        order.ApplyGroupDiscounts(discounts, occurredAt);
        return discounts;
    }
}
