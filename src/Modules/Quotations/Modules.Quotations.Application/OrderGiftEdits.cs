using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Lleva los regalos del pedido a la lista deseada (spec 2026-11-23). Corre después de <see cref="OrderItemEdits"/>, que sólo
/// ve las líneas pagadas, y lo comparten el guardado y el cálculo previo. La clave es la variante: el dominio garantiza un
/// regalo por variante, y puede coincidir con una línea pagada. Mismo orden que las líneas pagadas: altas, cambios, bajas.
/// <para><c>desired == null</c> no toca nada: un frontend viejo no manda <c>gifts</c> y no debe borrar los guardados. Una
/// lista vacía los quita todos.</para>
/// <para>Un regalo nuevo toma el precio de lista del catálogo con <c>isRetail: true</c> (sin escalas); cambiar la cantidad no lee el catálogo ni reprecia: el precio y la tasa quedan los guardados y el
/// descuento sigue en 100 %.</para>
/// </summary>
internal static class OrderGiftEdits
{
    public static async Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Order order,
        IReadOnlyCollection<OrderItemAddition>? desired,
        IQuotationProductPricingLookup pricingLookup,
        IOrderVariantCostLookup costLookup,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (desired is null)
        {
            return [];
        }

        var current = order.Items.Where(item => item.IsGift).ToDictionary(item => item.VariantId);
        var desiredVariantIds = desired.Select(line => line.VariantId).ToHashSet();
        var additions = desired.Where(line => !current.ContainsKey(line.VariantId)).ToArray();
        var edits = new List<OrderItemEdit>();

        // Una sola lectura del catálogo y de costos para todas las altas; los cambios y las bajas no leen nada.
        var products = additions.Length == 0
            ? new Dictionary<Guid, QuotationVariantPricingRef>()
            : await QuotationProductPricingResolver.FindVariantsAsync(
                pricingLookup, tenantId, additions.Select(line => line.VariantId), cancellationToken,
                QuotationPriceChannels.For(order.Source));
        var costs = await OrderLineCostsLoader.LoadForAdditionsAsync(
            costLookup, tenantId, order, additions.Select(line => line.VariantId), cancellationToken);

        foreach (var line in additions)
        {
            // isRetail: true salta las escalas a propósito: el regalo va a precio de lista y su descuento es el fijo del 100 %.
            var pricing = QuotationProductPricingResolver.Resolve(
                products.GetValueOrDefault(line.VariantId), tenantId, line.VariantId, line.Quantity,
                order.Currency, isRetail: true);
            order.AddGift(
                OrderItemId.New(), line.VariantId, pricing.ProductId, line.Quantity, pricing.Pricing.UnitPrice,
                pricing.Pricing.TaxPercentage, costs, updatedBy, occurredAt, pricing.Kind, pricing.SalesUnit);
            edits.Add(new OrderItemEdit(
                OrderItemEditKind.Added, line.VariantId, pricing.Name, null, line.Quantity, IsGift: true));
        }

        foreach (var line in desired)
        {
            if (!current.TryGetValue(line.VariantId, out var item) || item.Quantity == line.Quantity)
            {
                continue;
            }

            var previousQuantity = item.Quantity;
            order.UpdateItemQuantity(
                item.Id, line.Quantity, item.DiscountPercentage, item.ProductTaxPercentage ?? item.TaxPercentage,
                updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(
                OrderItemEditKind.QuantityChanged, line.VariantId, NameOf(item), previousQuantity, line.Quantity,
                IsGift: true));
        }

        foreach (var item in current.Values.Where(item => !desiredVariantIds.Contains(item.VariantId)).ToArray())
        {
            var name = NameOf(item);
            order.RemoveItem(item.Id, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Removed, item.VariantId, name, item.Quantity, null, IsGift: true));
        }

        return edits;
    }

    // El historial nombra el regalo con la foto de la línea, que no depende de que la variante siga en el catálogo.
    private static string NameOf(OrderItem item) => item.ProductName ?? item.ProductCode ?? "un producto";
}
