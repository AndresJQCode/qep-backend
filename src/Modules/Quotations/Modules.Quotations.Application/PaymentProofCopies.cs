using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Las copias públicas de los comprobantes nuevos de un request (spec 2026-09-15, P4 y P7), y
/// también del archivo de reemplazo cuando se corrige uno ya cargado
/// (<see cref="PublishReplacementAsync"/>, a pedido, 2026-09-15). La usan
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

    /// <summary>Publica el archivo de reemplazo de un comprobante ya cargado (a pedido,
    /// 2026-09-15) — mismo publicador, mismo rollback compartido que <see cref="PublishAsync"/>:
    /// si el request falla después de esta copia (por ejemplo, el pedido ya se aprobó), queda
    /// anotada para que <see cref="RollbackAsync"/> también la borre.</summary>
    public async Task<string?> PublishReplacementAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var publicKey = await publisher.PublishAsync(tenantId, fileId, cancellationToken);
        if (publicKey is not null)
        {
            _publicKeys.Add(publicKey);
        }

        return publicKey;
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

    /// <summary>Los comprobantes que quedaron con copia pública, para el evento de D9 (spec
    /// 2026-09-16). Uno sin clave —la opción apagada— no tiene nada que mover.</summary>
    public static AttachedPaymentProof[] AttachedFrom(IEnumerable<OrderPaymentProofInput> inputs) =>
        inputs
            .Where(input => input.PublicStorageKey is not null)
            .Select(input => new AttachedPaymentProof(input.FileId, input.PublicStorageKey!))
            .ToArray();

    /// <summary>Los archivos de reemplazo que quedaron con copia pública (spec 2026-09-16, D9 y D19):
    /// para Storage son comprobantes nuevos. Una corrección sólo de monto no trae archivo.</summary>
    public static AttachedPaymentProof[] AttachedFromReplacements(
        IEnumerable<OrderPaymentProofAmountUpdate> updates) =>
        updates
            .Where(update => update.NewFileId is not null && update.NewPublicStorageKey is not null)
            .Select(update => new AttachedPaymentProof(update.NewFileId!.Value, update.NewPublicStorageKey!))
            .ToArray();
}
