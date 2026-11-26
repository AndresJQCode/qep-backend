using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Los eventos de pedido de Quotations que Shopify consume. Copia del nombre que publica
/// <c>QuotationLifecycleEventPublisher</c>: Shopify no referencia Quotations (<c>ShopifyLayerTests</c>).</summary>
public static class ShopifyQuotationOrderEventNames
{
    public const string ShippingPartyChanged = "quotations.order.shipping-party-changed.v1";
}

/// <summary><c>quotations.order.shipping-party-changed.v1</c>: «Editar pedido» cambió la dirección de envío de un pedido de
/// Shopify. No lleva la dirección: el envío la lee del pedido al mandar.</summary>
public sealed record ShopifyShippingPartyChangedFact(Guid TenantId, Guid OrderId, string Source, DateTimeOffset OccurredAt);

/// <summary>
/// Spec 2026-11-24 §11.1.1: encola el envío de la dirección (<see cref="ShopifyPushGroup.ShippingAddress"/>) de un pedido que se
/// editó en QEP. No guarda: el consumidor guarda el envío y el inbox en el mismo <c>SaveChangesAsync</c>.
/// <list type="bullet">
/// <item>Sin fila en el espejo no hay a quién empujar.</item>
/// <item>Un envío abierto se fusiona (<see cref="ShopifyOrderPushes"/>); con la conexión no operativa queda <c>Held</c> con
/// <c>connection_inactive</c>, como una corrección del panel (owner, 2026-11-14).</item>
/// <item><b>No mueve <c>applied_projection</c></b>, como la corrección del asesor (plan D3): la edición siguiente de Shopify sobre
/// la dirección es conflicto y gana QEP. Tampoco escribe el pedido: ya lo escribió Quotations.</item>
/// </list>
/// </summary>
public sealed class ShopifyShippingPartyChangedApplier(
    IShopifyOrderMirrorRepository mirrors,
    IShopifyConnectionDirectory connections,
    ShopifyOrderPushes pushes,
    IClock clock)
{
    public async Task ApplyAsync(ShopifyShippingPartyChangedFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (await mirrors.FindByQepOrderIdAsync(fact.TenantId, fact.OrderId, cancellationToken) is not { } row)
        {
            return;
        }

        var holdReason = await connections.FindAsync(row.TenantId, row.ConnectionId, cancellationToken) is { IsOperational: true }
            ? null
            : ShopifyPushFailureCodes.ConnectionInactive;
        await pushes.EnqueueAsync(row, [ShopifyPushGroup.ShippingAddress], clock.UtcNow, cancellationToken, holdReason);
    }
}
