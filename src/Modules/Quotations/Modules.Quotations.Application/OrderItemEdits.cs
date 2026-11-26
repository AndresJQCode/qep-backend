using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal enum OrderItemEditKind
{
    Added,
    QuantityChanged,
    Removed,

    // D-4: sólo en un pedido mayorista.
    UnitPriceChanged,
}

internal sealed record OrderItemEdit(
    OrderItemEditKind Kind,
    Guid VariantId,
    string? ProductName,
    decimal? PreviousQuantity,
    decimal? Quantity,
    decimal? PreviousUnitPrice = null,
    decimal? UnitPrice = null,
    // Spec 2026-11-23: la edición es de una línea de regalo (OrderGiftEdits), con su propio texto y auditoría.
    bool IsGift = false);

/// <summary>
/// Lleva las líneas del pedido a la lista deseada (spec 2026-09-17 y spec 2026-10-15 §4.5): agrega
/// las que faltan, reprecia las que cambian de cantidad y quita las que sobran, sobre el pedido y
/// no sobre la cotización. Agrega antes de quitar para que la regla de «última línea» no se dispare
/// al reemplazar todos los productos. Una línea que no cambia no se toca ni se reprecia: si su
/// variante ya no existe en el catálogo, conserva su foto. La clave es <c>variantId</c> (spec
/// 2026-10-20 §4): dos tallas del mismo producto son dos líneas.
/// <para>Mayorista fase 3 (D-4, D-5): un pedido <see cref="OrderSource.Wholesale"/> va por su propia rama, sin escalas. La
/// línea nueva toma el precio de lista del distribuidor (<see cref="IWholesaleLinePricing"/>) o, sin él, el base del catálogo;
/// cambiar la cantidad conserva el precio; un <c>UnitPrice</c> explícito manda. En otro origen, mandar un precio es 422
/// <c>order.item.unit_price_not_editable</c>, antes de tocar nada.</para>
/// </summary>
internal static class OrderItemEdits
{
    public static async Task<IReadOnlyList<OrderItemEdit>> ApplyAsync(
        Order order,
        IReadOnlyCollection<OrderItemAddition> desired,
        IQuotationProductPricingLookup pricingLookup,
        IOrderVariantCostLookup costLookup,
        IWholesaleLinePricing wholesalePricing,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        bool isRetail,
        CancellationToken cancellationToken)
    {
        var wholesale = order.Source == OrderSource.Wholesale;
        if (!wholesale && desired.Any(line => line.UnitPrice is not null))
        {
            throw new QuotationsDomainException(
                WholesaleOrderErrors.UnitPriceNotEditable, "Only a wholesale order lets you change a line price.");
        }

        // Los regalos (spec 2026-11-21) no entran en la lista deseada del PUT: se agregan y se quitan por su propia ruta, y
        // pueden compartir variante con una línea pagada, así que ni se indexan ni se quitan acá.
        var current = order.Items.Where(item => !item.IsGift).ToDictionary(item => item.VariantId);
        var desiredVariantIds = desired.Select(line => line.VariantId).ToHashSet();
        var edits = new List<OrderItemEdit>();

        // Una sola lectura del catálogo para todo el diff, como en QuotationItemEdits: el cálculo
        // previo corre mientras se escribe, y una lectura por línea son varias sentencias por línea.
        // Los rechazos no se adelantan: siguen abajo, línea por línea, en el mismo orden. En el pedido mayorista la cantidad
        // no reprecia, así que sólo se leen las nuevas.
        var products = await QuotationProductPricingResolver.FindVariantsAsync(
            pricingLookup,
            tenantId,
            desired
                .Where(line => !current.TryGetValue(line.VariantId, out var item)
                    || (!wholesale && item.Quantity != line.Quantity))
                .Select(line => line.VariantId),
            cancellationToken,
            QuotationPriceChannels.For(order.Source));
        var additions = desired.Where(line => !current.ContainsKey(line.VariantId)).ToArray();
        var costs = await OrderLineCostsLoader.LoadForAdditionsAsync(
            costLookup,
            tenantId,
            order,
            additions.Select(line => line.VariantId),
            cancellationToken);
        var listPrices = await WholesaleListPricesAsync(
            wholesalePricing,
            order,
            tenantId,
            additions.Where(line => line.UnitPrice is null).Select(line => line.VariantId).ToArray(),
            cancellationToken);

        foreach (var line in additions)
        {
            var pricing = QuotationProductPricingResolver.Resolve(
                products.GetValueOrDefault(line.VariantId), tenantId, line.VariantId, line.Quantity,
                order.Currency, isRetail);
            var unitPrice = wholesale
                ? line.UnitPrice ?? listPrices.GetValueOrDefault(line.VariantId, pricing.Pricing.UnitPrice)
                : pricing.Pricing.UnitPrice;
            order.AddItem(
                OrderItemId.New(), line.VariantId, pricing.ProductId, line.Quantity,
                unitPrice, wholesale ? 0m : pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage, costs, updatedBy, occurredAt, pricing.Kind, pricing.SalesUnit);
            edits.Add(new OrderItemEdit(OrderItemEditKind.Added, line.VariantId, pricing.Name, null, line.Quantity));
        }

        foreach (var line in desired)
        {
            if (!current.TryGetValue(line.VariantId, out var item))
            {
                continue;
            }

            if (wholesale)
            {
                ApplyWholesaleChanges(order, item, line, updatedBy, occurredAt, edits);
                continue;
            }

            if (item.Quantity == line.Quantity)
            {
                continue;
            }

            var previousQuantity = item.Quantity;
            var pricing = QuotationProductPricingResolver.Resolve(
                products.GetValueOrDefault(line.VariantId), tenantId, line.VariantId, line.Quantity,
                order.Currency, isRetail);
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

    /// <summary>
    /// D-5: los precios de lista del distribuidor del pedido mayorista para esas variantes; vacío fuera de un pedido mayorista,
    /// sin variantes o sin comprador (un pedido así no debería existir, pero entonces rige el precio base).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, decimal>> WholesaleListPricesAsync(
        IWholesaleLinePricing wholesalePricing,
        Order order,
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken) =>
        order is { Source: OrderSource.Wholesale, BuyerTenantId: { } buyerTenantId } && variantIds.Count > 0
            ? await wholesalePricing.ListPricesAsync(tenantId, buyerTenantId, order.Currency, variantIds, cancellationToken)
            : new Dictionary<Guid, decimal>();

    // La cantidad cambia sin repreciar (descuento 0 y la tasa del producto que la línea ya tiene); el precio sólo si llega uno
    // distinto. Sin leer el catálogo: una variante que ya no existe no impide corregir su cantidad.
    private static void ApplyWholesaleChanges(
        Order order,
        OrderItem item,
        OrderItemAddition line,
        MemberId updatedBy,
        DateTimeOffset occurredAt,
        List<OrderItemEdit> edits)
    {
        if (item.Quantity != line.Quantity)
        {
            var previousQuantity = item.Quantity;
            order.UpdateItemQuantity(
                item.Id, line.Quantity, item.DiscountPercentage, item.ProductTaxPercentage ?? item.TaxPercentage,
                updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(
                OrderItemEditKind.QuantityChanged, line.VariantId, NameOf(item), previousQuantity, line.Quantity));
        }

        if (line.UnitPrice is { } unitPrice && unitPrice != item.UnitPrice)
        {
            var previousUnitPrice = item.UnitPrice;
            order.ChangeItemUnitPrice(item.Id, unitPrice, updatedBy, occurredAt);
            edits.Add(new OrderItemEdit(
                OrderItemEditKind.UnitPriceChanged, line.VariantId, NameOf(item), item.Quantity, item.Quantity,
                previousUnitPrice, item.UnitPrice));
        }
    }

    // El historial nombra la línea con su foto; la del camino mayorista siempre se captura al crear.
    private static string NameOf(OrderItem item) => item.ProductName ?? item.ProductCode ?? "un producto";
}
