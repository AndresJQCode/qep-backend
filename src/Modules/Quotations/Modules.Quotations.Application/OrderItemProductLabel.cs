using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>La foto de código y nombre de las líneas del pedido que todavía no la tienen (las que
/// se sumaron después de crearlo), como <see cref="QuotationItemProductLabel.ResolveMissingAsync"/>.</summary>
internal static class OrderItemProductLabel
{
    public static async Task<IReadOnlyDictionary<Guid, QuotationProductSnapshot>> ResolveMissingAsync(
        IQuotationProductLookup productLookup,
        Guid tenantId,
        Order order,
        CancellationToken cancellationToken)
    {
        var missing = order.Items
            .Where(item => item.ProductCode is null)
            .Select(item => item.ProductId)
            .Distinct()
            .ToArray();
        if (missing.Length == 0)
        {
            return QuotationProductSnapshot.None;
        }

        var products = await productLookup.FindManyAsync(tenantId, missing, cancellationToken);
        return products.ToDictionary(
            entry => entry.Key,
            entry => new QuotationProductSnapshot(entry.Value.Code, entry.Value.Name));
    }
}
