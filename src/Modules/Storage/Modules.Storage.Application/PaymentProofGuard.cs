using BuildingBlocks.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

// Spec 2026-09-16, D15. La copia pública de un comprobante movido es la que enlaza el Excel de
// pedidos: la evidencia del pago. Los endpoints genéricos de Storage no la pueden borrar mientras un
// pedido lo referencie, y un comprobante nunca se publica por ellos: sólo llega al público por el
// movimiento de la sección 2. Vive en Application y no en FileResource porque quién referencia un
// archivo lo sabe cada módulo, por IFileReferenceProbe.
internal static class PaymentProofGuard
{
    public static async Task EnsureNotReferencedAsync(
        FileResource resource,
        IEnumerable<IFileReferenceProbe> probes,
        CancellationToken cancellationToken)
    {
        if (resource.OwnerType is not FileOwnerType.PaymentProof)
        {
            return;
        }

        // Secuencial y cortando en la primera que retiene, igual que StagingCleanupProcessor.
        foreach (var probe in probes)
        {
            if (await probe.HasReferencesAsync(resource.Id.Value, cancellationToken))
            {
                throw new StorageDomainException(
                    "storage.file.invalid_state",
                    $"The payment proof is still referenced by {probe.Source} and cannot be deleted or unpublished.");
            }
        }
    }

    public static void EnsureNotPaymentProof(FileResource resource)
    {
        if (resource.OwnerType is FileOwnerType.PaymentProof)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "A payment proof reaches the public bucket only when it is attached to an order.");
        }
    }
}
