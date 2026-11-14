using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-14 §5: encolar lo que viaja a Shopify. Lo llaman la corrección del asesor (<see cref="CorrectShopifyOrderHandler"/>)
/// y la corrección de ciudad del sistema (<see cref="ShopifyCityCorrectionProcessor"/>), siempre dentro de su propia unidad de
/// trabajo: el envío se guarda en el mismo <c>SaveChangesAsync</c> que lo que lo causó, y el commit es del llamador.
/// </summary>
public sealed class ShopifyOrderPushes(IShopifyOrderPushRepository pushes)
{
    /// <summary>Fusiona con el <c>Pending</c> de la fila o agrega uno. Dos encolados a la vez sobre una fila sin pendiente chocan con
    /// <c>IX_order_pushes_mirror_pending</c> al guardar (<c>concurrency.conflict</c>).</summary>
    public async Task EnqueueAsync(
        ShopifyOrderMirror row, IReadOnlyCollection<ShopifyPushGroup> groups, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(groups);
        if (await pushes.FindPendingAsync(row.TenantId, row.Id, cancellationToken) is { } pending)
        {
            pending.Merge(groups, now);
            return;
        }

        pushes.Add(ShopifyOrderPush.Create(row, groups, now));
    }
}
