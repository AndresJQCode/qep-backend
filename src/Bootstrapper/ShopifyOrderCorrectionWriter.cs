using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-14 §5: la corrección del asesor sobre un pedido de Shopify (<c>PUT …/shopify/orders/{id}/corrections</c>) escribe el
/// pedido de QEP por <see cref="IExternalOrderWriter.CorrectByMemberAsync"/>. Vive en el composition root por la misma razón que
/// <see cref="CatalogProductDeletionCleanup"/>: Shopify no referencia Quotations (<c>ShopifyLayerTests</c>).
///
/// **Corre sobre la conexión y la transacción del <see cref="ShopifyDbContext"/> del scope.** El <see cref="QuotationsDbContext"/> del
/// mismo scope se enrola en ellas antes de escribir, así que el pedido, los atributos de la fila del espejo y el envío encolado
/// (<c>shopify.order_pushes</c>) quedan en una sola transacción: si algo de Shopify falla después de escribir el pedido, no queda
/// nada. Por eso, al revés que <see cref="ShopifyOrderWriter"/>, **no** abre un scope propio: con otro <c>DbContext</c> la palabra
/// «transacción» no significaría nada. Sin transacción abierta lanza <see cref="InvalidOperationException"/>.
///
/// El enrolamiento dura lo que el scope: después del commit, ese <see cref="QuotationsDbContext"/> sigue apuntando a una transacción
/// terminada, así que **nada en el mismo scope debe volver a usarlo**. El handler no relee el pedido después del commit: arma la
/// respuesta con el estado que devuelve <see cref="CorrectAsync"/>. Un camino nuevo que corrija un pedido de Shopify y encole su
/// envío pasa por acá.
/// </summary>
internal sealed class ShopifyOrderCorrectionWriter(
    ShopifyDbContext shopify,
    QuotationsDbContext quotations,
    IExternalOrderWriter orders,
    IClock clock) : IShopifyOrderCorrectionWriter
{
    public async Task<ShopifyOrderState> CorrectAsync(ShopifyOrderCorrection correction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(correction);
        var transaction = shopify.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A Shopify order correction must run inside the Shopify transaction.");

        quotations.Database.SetDbConnection(shopify.Database.GetDbConnection(), contextOwnsConnection: false);
        await quotations.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
        var state = await orders.CorrectByMemberAsync(
            new ExternalOrderCorrection(
                correction.TenantId,
                correction.OrderId,
                correction.ExpectedVersion,
                correction.ShippingParty is not { } party ? null : ShopifyOrderWriter.ToExternalParty(party),
                correction.ReplaceBuyerNotes,
                correction.BuyerNotes,
                correction.MemberId,
                correction.SubjectId,
                ShopifyOrderImporter.RawAddressLabel,
                clock.UtcNow),
            cancellationToken);
        return ShopifyOrderWriter.ToShopify(state);
    }
}
