using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.ObjectStorage;

internal interface IStagingCleanupProcessor
{
    Task CleanupAsync(CancellationToken cancellationToken);
}

// El barrido de staging/, fuera del BackgroundService para poder probarlo sin esperar al
// temporizador, mismo criterio que QuotationExpirationProcessor. Dos búsquedas con el mismo reloj y
// la misma retención: las subidas abandonadas de siempre y, desde el spec 2026-09-16 (D11), los
// comprobantes de pago que siguen en staging/ sin que nadie los adjunte.
internal sealed partial class StagingCleanupProcessor(
    StorageDbContext dbContext,
    IObjectStorage objectStorage,
    IStorageAuditPublisher auditPublisher,
    IEnumerable<IFileReferenceProbe> probes,
    IClock clock,
    IOptions<StorageOptions> options,
    ILogger<StagingCleanupProcessor> logger) : IStagingCleanupProcessor
{
    private const int BatchSize = 100;

    private readonly List<IFileReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof {FileId} kept in staging: still referenced by {Source}.")]
    private static partial void LogProofRetained(ILogger logger, Guid fileId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof {FileId} purged from staging: no module references it.")]
    private static partial void LogProofPurged(ILogger logger, Guid fileId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payment proof {FileId} could not be purged from staging: invalid state.")]
    private static partial void LogProofRejected(ILogger logger, Guid fileId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Payment proof {FileId} could not be purged from staging; the next tick retries it.")]
    private static partial void LogProofFailed(ILogger logger, Guid fileId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No file reference probe is registered: unattached payment proofs are not purged.")]
    private static partial void LogNoProbes(ILogger logger);

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddHours(-options.Value.StagingRetentionHours);
        await PurgeAbandonedUploadsAsync(cutoff, cancellationToken);
        await PurgeUnattachedPaymentProofsAsync(cutoff, cancellationToken);
    }

    private async Task PurgeAbandonedUploadsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var abandoned = await dbContext.FileResources
            .Where(file => file.Status == FileResourceStatus.PendingUpload)
            .Where(file => file.CreatedAt < cutoff)
            .OrderBy(file => file.CreatedAt)
            .Take(BatchSize)
            .ToArrayAsync(cancellationToken);

        foreach (var file in abandoned)
        {
            await objectStorage.DeleteAsync(file.StorageKey, cancellationToken);
            file.PurgeAbandonedUpload(clock.UtcNow);
        }
        if (abandoned.Length > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task PurgeUnattachedPaymentProofsAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // Sin sondas no hay forma de saber si un comprobante está adjunto: purgar sería borrar
        // comprobantes de pedidos. Se falla cerrado y se avisa.
        if (_probes.Count == 0)
        {
            LogNoProbes(logger);
            return;
        }

        // Un comprobante retenido, o uno cuya purga falló, sigue cumpliendo el filtro en la vuelta
        // siguiente, así que se saltan los ya vistos: sin esto, cien retenidos viejos (por ejemplo, con
        // Quotations:PaymentProofs:PublicLinks apagada, donde nunca se mueven) taparían para siempre a
        // los huérfanos más nuevos. Los purgados salen del filtro porque cada uno se guarda enseguida.
        var skipped = 0;
        while (true)
        {
            var batch = await dbContext.FileResources
                .Where(file => file.OwnerType == FileOwnerType.PaymentProof)
                .Where(file => file.Status == FileResourceStatus.Available)
                .Where(file => file.PublicStorageKey == null)
                .Where(file => file.CreatedAt < cutoff)
                .OrderBy(file => file.CreatedAt)
                .ThenBy(file => file.Id)
                .Skip(skipped)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);

            foreach (var file in batch)
            {
                if (!await TryPurgeUnattachedPaymentProofAsync(file, cancellationToken))
                {
                    skipped++;
                }
            }

            if (batch.Length < BatchSize)
            {
                return;
            }
        }
    }

    // Un comprobante por vez, con su propio SaveChanges: si el borrado del siguiente falla, los ya
    // borrados de staging/ no quedan Available sin objeto (se podrían adjuntar o descargar). Devuelve
    // false si el comprobante sigue en el filtro: retenido por una sonda o con una purga fallida.
    private async Task<bool> TryPurgeUnattachedPaymentProofAsync(
        FileResource file, CancellationToken cancellationToken)
    {
        var fileId = file.Id.Value;
        try
        {
            var retainedBy = await FindRetainingSourceAsync(fileId, cancellationToken);
            if (retainedBy is not null)
            {
                // Un comprobante adjunto cuyo evento de D9 todavía no se procesó.
                LogProofRetained(logger, fileId, retainedBy);
                return false;
            }

            // Mismo orden que el movimiento (D9): primero el objeto, después la fila. Si guardar
            // falla, el tick siguiente borra una clave que ya no existe y guarda.
            await objectStorage.DeleteAsync(file.StorageKey, cancellationToken);
            var now = clock.UtcNow;
            file.PurgeUnattachedPaymentProof(now);
            auditPublisher.PublishSystem(
                file.TenantId,
                "storage.file.purged",
                "file",
                file.Id.ToString(),
                "payment_proof_not_attached",
                now);
            await dbContext.SaveChangesAsync(cancellationToken);
            LogProofPurged(logger, fileId);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageDomainException exception)
        {
            LogProofRejected(logger, fileId, exception);
            DiscardPendingChanges();
            return false;
        }
        catch (Exception exception)
        {
            LogProofFailed(logger, fileId, exception);
            DiscardPendingChanges();
            return false;
        }
    }

    // Los comprobantes anteriores ya se guardaron, así que lo pendiente es sólo del que falló: su fila
    // y su auditoría. Se sueltan para que no viajen en el SaveChanges del siguiente.
    private void DiscardPendingChanges()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // Secuencial y cortando en la primera que retiene, igual que OrphanUserCleanupWorker.
    private async Task<string?> FindRetainingSourceAsync(Guid fileId, CancellationToken cancellationToken)
    {
        foreach (var probe in _probes)
        {
            if (await probe.HasReferencesAsync(fileId, cancellationToken))
            {
                return probe.Source;
            }
        }

        return null;
    }
}
