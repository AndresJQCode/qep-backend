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

    private readonly IReadOnlyList<IFileReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof {FileId} kept in staging: still referenced by {Source}.")]
    private static partial void LogProofRetained(ILogger logger, Guid fileId, string source);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof {FileId} purged from staging: no module references it.")]
    private static partial void LogProofPurged(ILogger logger, Guid fileId);

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
        // Un comprobante retenido sigue cumpliendo el filtro en la vuelta siguiente, así que se saltean
        // los ya vistos: sin esto, cien retenidos viejos (por ejemplo, con
        // Quotations:PaymentProofs:PublicLinks apagada, donde nunca se mueven) taparían para siempre a
        // los huérfanos más nuevos. Los purgados salen del filtro al guardar cada lote.
        var retained = 0;
        while (true)
        {
            var batch = await dbContext.FileResources
                .Where(file => file.OwnerType == FileOwnerType.PaymentProof)
                .Where(file => file.Status == FileResourceStatus.Available)
                .Where(file => file.PublicStorageKey == null)
                .Where(file => file.CreatedAt < cutoff)
                .OrderBy(file => file.CreatedAt)
                .ThenBy(file => file.Id)
                .Skip(retained)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);

            foreach (var file in batch)
            {
                var retainedBy = await FindRetainingSourceAsync(file.Id.Value, cancellationToken);
                if (retainedBy is not null)
                {
                    // Un comprobante adjunto cuyo evento de D9 todavía no se procesó.
                    LogProofRetained(logger, file.Id.Value, retainedBy);
                    retained++;
                    continue;
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
                LogProofPurged(logger, file.Id.Value);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (batch.Length < BatchSize)
            {
                return;
            }
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
