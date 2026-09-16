using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.Infrastructure.PaymentProofs;

internal interface IPaymentProofOrphanCleanupProcessor
{
    Task<PaymentProofOrphanCleanupResult> CleanupAsync(CancellationToken cancellationToken);
}

/// <summary>Lo que hizo una corrida: los objetos listados bajo el prefijo, los que se saltaron por
/// recientes o referenciados, los huérfanos y los que efectivamente se borraron.</summary>
internal sealed record PaymentProofOrphanCleanupResult(
    int Listed,
    int Recent,
    int Referenced,
    int Orphans,
    int Deleted);

// Spec 2026-09-16, D12 y sección 4. Recorre sólo payment-proofs/ del bucket público y borra lo que
// ningún módulo referencia. Los objetos de ahí pueden estar enlazados en un Excel ya enviado, así que
// se salta lo reciente (al adjuntar se copia antes de guardar el pedido, D9) y, con DryRun, sólo se
// registra. Qué referencia un objeto lo decide cada módulo por IPublicObjectReferenceProbe: este
// procesador no conoce a Quotations.
//
// Los errores siguen el criterio del barrido de staging (Task 9): un objeto cuya sonda o cuyo borrado
// falla se registra como Error, se descarta lo pendiente y se sigue con el siguiente; como no se borró,
// la corrida siguiente lo vuelve a listar. Un listado que falla corta la corrida y lo registra el worker.
internal sealed partial class PaymentProofOrphanCleanupProcessor(
    StorageDbContext dbContext,
    IPublicObjectStorage publicObjectStorage,
    IEnumerable<IPublicObjectReferenceProbe> probes,
    IStorageAuditPublisher auditPublisher,
    IClock clock,
    IOptions<StorageOptions> options,
    ILogger<PaymentProofOrphanCleanupProcessor> logger) : IPaymentProofOrphanCleanupProcessor
{
    // Con la barra: sin ella también entraría "payment-proofs-viejos/". Mismo prefijo que
    // PublicPaymentProofPublisher. Nunca tenants/.../media (imágenes de producto) ni quotations/.
    internal const string Prefix = "payment-proofs/";

    private readonly List<IPublicObjectReferenceProbe> _probes = probes.ToList();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof orphan cleanup skipped: the public bucket is not configured.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No public object reference probe is registered: payment proof orphan cleanup deletes nothing.")]
    private static partial void LogNoProbes(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payment proof orphan cleanup (dry run) would delete {Key}, last modified {LastModified}.")]
    private static partial void LogWouldDelete(ILogger logger, string key, DateTimeOffset lastModified);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payment proof orphan cleanup deleted {Key}, last modified {LastModified}.")]
    private static partial void LogDeleted(ILogger logger, string key, DateTimeOffset lastModified);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Payment proof orphan cleanup could not process {Key}; the next run retries it.")]
    private static partial void LogObjectFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Payment proof orphan cleanup finished: {Listed} listed, {Recent} recent, {Referenced} referenced, {Orphans} orphans, {Deleted} deleted (dry run: {DryRun}).")]
    private static partial void LogFinished(
        ILogger logger, int listed, int recent, int referenced, int orphans, int deleted, bool dryRun);

    public async Task<PaymentProofOrphanCleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        // Sin bucket público no hay nada que recorrer, y el adaptador llamaría a R2 igual.
        if (!publicObjectStorage.IsConfigured)
        {
            LogNotConfigured(logger);
            return new PaymentProofOrphanCleanupResult(0, 0, 0, 0, 0);
        }

        // Sin sondas todo parecería huérfano: borrar sería romper los enlaces de los Excels ya enviados.
        // Se falla cerrado, igual que StagingCleanupProcessor y PaymentProofDetachProcessor, y ni
        // siquiera se lista, así que el modo en seco tampoco reporta falsos huérfanos.
        if (_probes.Count == 0)
        {
            LogNoProbes(logger);
            return new PaymentProofOrphanCleanupResult(0, 0, 0, 0, 0);
        }

        var settings = options.Value.PaymentProofOrphanCleanup;
        var cutoff = clock.UtcNow.AddHours(-settings.MinimumAgeHours);
        int listed = 0, recent = 0, referenced = 0, orphans = 0, deleted = 0;
        string? continuationToken = null;
        do
        {
            var page = await publicObjectStorage.ListAsync(Prefix, continuationToken, cancellationToken);
            foreach (var stored in page.Objects)
            {
                // Defensa: si el adaptador devolviera algo fuera del prefijo, no se toca.
                if (!stored.Key.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                listed++;
                if (stored.LastModified > cutoff)
                {
                    recent++;
                    continue;
                }

                try
                {
                    if (await IsReferencedAsync(stored.Key, cancellationToken))
                    {
                        referenced++;
                        continue;
                    }

                    orphans++;
                    if (settings.DryRun)
                    {
                        LogWouldDelete(logger, stored.Key, stored.LastModified);
                        continue;
                    }

                    await publicObjectStorage.DeleteAsync(stored.Key, cancellationToken);
                    // Auditado y guardado por objeto, no al final: si el proceso muere a mitad del
                    // recorrido, lo ya borrado queda auditado.
                    auditPublisher.PublishSystem(
                        tenantId: null,
                        "storage.public_object.purged",
                        "public_object",
                        stored.Key,
                        "success",
                        clock.UtcNow);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    LogDeleted(logger, stored.Key, stored.LastModified);
                    deleted++;
                }
                // Sólo el apagado del host corta la corrida. Otra cancelación (un timeout de R2 que llega
                // como TaskCanceledException) es un fallo transitorio más de este objeto.
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogObjectFailed(logger, stored.Key, exception);
                    DiscardPendingChanges();
                }
            }

            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        LogFinished(logger, listed, recent, referenced, orphans, deleted, settings.DryRun);
        return new PaymentProofOrphanCleanupResult(listed, recent, referenced, orphans, deleted);
    }

    // Lo guardado de los objetos anteriores ya está en la base; lo pendiente es sólo la auditoría del que
    // falló, que no debe viajar en el SaveChanges del siguiente.
    private void DiscardPendingChanges()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // Secuencial y cortando en la primera que retiene, igual que StagingCleanupProcessor.
    private async Task<bool> IsReferencedAsync(string publicStorageKey, CancellationToken cancellationToken)
    {
        foreach (var probe in _probes)
        {
            if (await probe.HasReferencesAsync(publicStorageKey, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
