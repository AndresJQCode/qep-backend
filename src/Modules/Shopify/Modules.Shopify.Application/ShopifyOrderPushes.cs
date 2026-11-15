using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-14 §5: encolar lo que viaja a Shopify. Lo llaman la corrección del asesor (<see cref="CorrectShopifyOrderHandler"/>)
/// y la corrección de ciudad del sistema (<see cref="ShopifyCityCorrectionProcessor"/>), siempre dentro de su propia unidad de
/// trabajo: el envío se guarda en el mismo <c>SaveChangesAsync</c> que lo que lo causó, y el commit es del llamador.
/// </summary>
public sealed class ShopifyOrderPushes(IShopifyOrderPushRepository pushes)
{
    /// <summary>
    /// Fusiona con el envío abierto de la fila (<c>Pending</c> o <c>Held</c>) o agrega uno, y devuelve el envío. Dos encolados a la vez
    /// sobre una fila sin envío abierto chocan con <c>IX_order_pushes_mirror_pending</c> al guardar (<c>concurrency.conflict</c>).
    /// <para>Owner 2026-11-14: con <paramref name="holdReason"/> (la conexión no está operativa) el envío queda retenido
    /// (<c>Held</c>), también si ya estaba pendiente: el worker sólo lo retendría al mandarlo. Sin él, un envío retenido sigue
    /// retenido aunque reciba más grupos: lo sueltan sólo los dos endpoints de sincronización, que miran la conexión.</para>
    /// </summary>
    public async Task<ShopifyOrderPush> EnqueueAsync(
        ShopifyOrderMirror row,
        IReadOnlyCollection<ShopifyPushGroup> groups,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? holdReason = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(groups);
        var push = await pushes.FindOpenAsync(row.TenantId, row.Id, cancellationToken);
        if (push is not null)
        {
            push.Merge(groups, now);
        }
        else
        {
            push = ShopifyOrderPush.Create(row, groups, now);
            pushes.Add(push);
        }

        if (holdReason is not null)
        {
            push.Hold(holdReason, now);
        }

        return push;
    }
}
