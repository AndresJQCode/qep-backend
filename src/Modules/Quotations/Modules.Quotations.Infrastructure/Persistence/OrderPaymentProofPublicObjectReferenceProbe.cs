using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Quotations retiene un objeto público mientras algún comprobante de pago guarde su clave (spec
/// 2026-09-16, D12): es la copia que enlaza el Excel de pedidos, de v1 o de v2. La consulta usa el
/// índice <c>IX_order_payment_proofs_public_key</c> (D17).
/// </summary>
internal sealed class OrderPaymentProofPublicObjectReferenceProbe(QuotationsDbContext dbContext)
    : IPublicObjectReferenceProbe
{
    public string Source => "quotations";

    public Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken) =>
        dbContext.OrderPaymentProofs.AnyAsync(
            proof => proof.PublicStorageKey == publicStorageKey,
            cancellationToken);
}
