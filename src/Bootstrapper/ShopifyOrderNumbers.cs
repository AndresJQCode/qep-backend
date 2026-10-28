using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §7: el <c>PED-…</c> de los pedidos de una página de la bandeja, con una sola consulta sobre
/// <see cref="IOrderRepository"/>. Shopify no referencia Quotations: el composition root los une.</summary>
internal sealed class ShopifyOrderNumbers(IOrderRepository orders) : IShopifyOrderNumbers
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        var numbers = await orders.FindOrderNumbersAsync(tenantId, [.. orderIds.Select(id => new OrderId(id))], cancellationToken);
        return numbers.ToDictionary(pair => pair.Key.Value, pair => pair.Value);
    }
}
