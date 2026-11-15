using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Messaging.Application;
using Modules.Pos.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-29 §8.3: <c>search_catalog</c> reusa el adaptador de Catalog de la caja (<see cref="IPosProductLookup"/>), que ya resuelve
/// variantes vendibles con su nombre visible y sus precios con IVA. El precio es el de la moneda por defecto del tenant; una variante sin
/// precio en esa moneda vuelve con <c>price: null</c>, no se esconde.
/// </summary>
internal sealed class AiCatalogSearch(IPosProductLookup products, ITenantDefaultCurrency currencies) : IAiCatalogSearch
{
    public async Task<IReadOnlyList<AiCatalogItem>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken)
    {
        var currency = await currencies.GetAsync(tenantId, cancellationToken);
        var (items, _) = await products.SearchAsync(tenantId, query, categoryId: null, page: 1, pageSize: limit, cancellationToken);
        return
        [
            .. items
                .SelectMany(product => product.Variants)
                .Where(variant => variant.IsActive)
                .Take(limit)
                .Select(variant => new AiCatalogItem(
                    variant.VariantId,
                    variant.Name,
                    variant.Code,
                    variant.Prices.TryGetValue(currency, out var price) ? price : null,
                    currency,
                    TaxIncluded: true,
                    Active: true)),
        ];
    }
}

/// <summary>
/// Spec 2026-10-29 §8.3: los pedidos de un cliente, sin sesión. Lee <c>QuotationsDbContext</c> desde Bootstrapper con EF tipado (como
/// Pipeline lee Mensajería), en vez del <c>IExternalOrderReader</c> que proponía el spec: es una consulta de sólo lectura y un renombre en el
/// pedido rompe la compilación aquí, no en ejecución. Siempre filtrado por tenant <b>y</b> cliente: un número de otro cliente no aparece,
/// igual que uno que no existe.
/// </summary>
internal sealed class AiOrderStatusReader(QuotationsDbContext quotations) : IAiOrderStatusReader
{
    public async Task<IReadOnlyList<AiOrderStatus>> ListAsync(
        Guid tenantId, Guid customerId, string? orderNumber, int limit, CancellationToken cancellationToken)
    {
        var orders = quotations.Orders.AsNoTracking().Where(order => order.TenantId == tenantId && order.ClientId == customerId);
        if (orderNumber is not null)
        {
            orders = orders.Where(order => order.OrderNumber == orderNumber);
        }

        var rows = await orders
            .OrderByDescending(order => order.ConvertedAt)
            .Take(limit)
            .Select(order => new { order.OrderNumber, order.Status, order.PaymentStatus, order.ConvertedAt, order.Total, order.Currency })
            .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(row => new AiOrderStatus(
                row.OrderNumber, row.Status.ToString(), row.PaymentStatus.ToString(), row.ConvertedAt, row.Total, row.Currency)),
        ];
    }
}

/// <summary>§8.3: las respuestas rápidas de Mensajería como fuente del agente; el id citable es el de la respuesta.</summary>
internal sealed class AiQuickReplies(IQuickReplyRepository quickReplies) : IAiQuickReplies
{
    public async Task<IReadOnlyList<AiQuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [.. (await quickReplies.ListAsync(tenantId, cancellationToken)).Select(reply => new AiQuickReply(reply.Id.ToString(), reply.Shortcut, reply.Body))];
}
