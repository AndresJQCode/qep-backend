using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Quotations retiene un archivo mientras algún comprobante de pago lo referencia (spec 2026-09-16,
/// D11), sea cual sea el estado del pedido. Storage la consulta antes de purgar un comprobante que
/// sigue en staging/: si responde true, el comprobante está adjunto y sólo falta que se procese su
/// movimiento. La consulta usa el índice <c>IX_order_payment_proofs_file</c> (D17).
/// </summary>
internal sealed class OrderPaymentProofFileReferenceProbe(QuotationsDbContext dbContext)
    : IFileReferenceProbe
{
    public string Source => "quotations";

    public Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken) =>
        dbContext.OrderPaymentProofs.AnyAsync(proof => proof.FileId == fileId, cancellationToken);
}
