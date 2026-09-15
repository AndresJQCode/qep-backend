using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Las copias públicas de los comprobantes nuevos de un request (spec 2026-09-15, P4 y P7). La usan
/// <see cref="ConvertQuotationToOrderHandler"/> y <see cref="AddOrderPaymentProofsHandler"/>: cada
/// uno publica antes de tocar el dominio —<see cref="OrderPaymentProof"/> recibe la clave al
/// crearse— y, si algo falla después de la primera copia, llama a <see cref="RollbackAsync"/> y
/// relanza. Ese "algo" incluye un rechazo del dominio, como <c>order.order.not_pending</c>: así el
/// handler no duplica las reglas del dominio para decidir si publicar.
/// </summary>
internal sealed class PaymentProofCopies(IPaymentProofPublisher publisher)
{
    private readonly List<string> _publicKeys = [];

    /// <summary>Publica cada comprobante en orden y devuelve los inputs del dominio con su clave
    /// (null con la opción apagada). Si una copia falla, las anteriores quedan anotadas para el
    /// rollback.</summary>
    public async Task<OrderPaymentProofInput[]> PublishAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderPaymentProofRequest> proofs,
        CancellationToken cancellationToken)
    {
        var inputs = new List<OrderPaymentProofInput>(proofs.Count);
        foreach (var proof in proofs)
        {
            var publicKey = await publisher.PublishAsync(tenantId, proof.FileId, cancellationToken);
            if (publicKey is not null)
            {
                _publicKeys.Add(publicKey);
            }

            inputs.Add(new OrderPaymentProofInput(proof.FileId, proof.Amount, publicKey));
        }

        return inputs.ToArray();
    }

    /// <summary>Borra las copias hechas, best-effort, mismo criterio que el rollback de
    /// <c>PublishFileHandler</c> (SetFilePublication.cs): cada una en su propio try, para que una
    /// que falle no deje las demás, y con <see cref="CancellationToken.None"/>, porque el request que
    /// se canceló es justo el que dejó las copias.</summary>
    public async Task RollbackAsync()
    {
        foreach (var publicKey in _publicKeys)
        {
            try
            {
                await publisher.DeleteAsync(publicKey, CancellationToken.None);
            }
            catch
            {
                // Best-effort: una copia que no se pudo borrar queda huérfana en el bucket público,
                // y relanzar desde acá taparía la excepción original del request.
            }
        }
    }
}
