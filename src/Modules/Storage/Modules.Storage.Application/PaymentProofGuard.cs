using System.Linq;
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

        // Fix round 1 (I1): sin sondas registradas no hay forma de saber si un pedido lo referencia.
        // Fallar cerrado, igual que StagingCleanupProcessor cuando no hay sondas para el barrido: no
        // se puede tratar "nadie contestó" como "nadie lo referencia".
        var probeList = probes as ICollection<IFileReferenceProbe> ?? probes.ToList();
        if (probeList.Count == 0)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "No file reference probe is registered; the payment proof cannot be deleted or " +
                "unpublished safely.");
        }

        // Secuencial y cortando en la primera que retiene, igual que StagingCleanupProcessor.
        foreach (var probe in probeList)
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
