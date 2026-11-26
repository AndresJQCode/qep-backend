using Microsoft.Extensions.Logging;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.UnreferencedFiles;

/// <summary>Un barrido diario de archivos de sistema que ya nadie usa. Uno por dueño; los corre todos <see cref="UnreferencedFileSweepWorker"/>.</summary>
internal interface IUnreferencedFileSweep
{
    FileOwnerType OwnerType { get; }

    /// <returns>Cuántos archivos soltó.</returns>
    Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

// Spec 2026-11-17, «Closing the residual gaps», generalizado por el spec 2026-11-18 («Lifecycle»): la red final para los caminos que sólo
// registran (la revisión de uso que falla, el aviso del outbox que no se escribe, una subida que nunca se usó). Toma los archivos vivos de un
// dueño con más de 24 h, le pregunta por tenant al módulo que los usa cuáles siguen en uso y suelta el resto con UnreferencedFileReleaser.
//
// Recorre por cursor (created_at, id) en páginas de BatchSize hasta el final: los archivos en uso son los más viejos y nunca salen del
// conjunto, así que mirar sólo la primera página dejaría ciego al barrido en cuanto haya más de BatchSize vivos en total: la página es de
// todos los tenants, no de uno.
//
// Falla cerrado: si el módulo dueño no responde por un tenant, no se suelta nada de ese tenant en esa página. Un archivo que falla (R2, la
// base) se registra y el barrido sigue; mañana vuelve a aparecer.
internal abstract partial class UnreferencedFileSweep(
    IUnreferencedFileCandidates files,
    UnreferencedFileReleaser releaser,
    ILogger logger) : IUnreferencedFileSweep
{
    internal const int BatchSize = 200;
    internal static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unreferenced {OwnerType} sweep could not ask which files tenant {TenantId} still references; its files are kept.")]
    private static partial void LogReferencesFailed(ILogger logger, Exception exception, FileOwnerType ownerType, Guid tenantId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unreferenced {OwnerType} sweep could not release file {FileId}; the next sweep retries it.")]
    private static partial void LogFileFailed(ILogger logger, Exception exception, FileOwnerType ownerType, Guid fileId);

    public abstract FileOwnerType OwnerType { get; }

    /// <summary>El motivo de la auditoría <c>storage.file.deleted</c>.</summary>
    protected abstract string Reason { get; }

    /// <summary>El subconjunto de <paramref name="fileIds"/> que el tenant todavía usa.</summary>
    protected abstract Task<IReadOnlySet<Guid>> FindReferencedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);

    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var createdBefore = now - Grace;
        var released = 0;
        UnreferencedFileCandidate? after = null;
        while (true)
        {
            var page = await files.ListStaleAsync(OwnerType, createdBefore, after, BatchSize, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            after = page[^1];
            // La consulta ya filtra por dueño; un archivo de otro dueño nunca se pregunta ni se suelta aunque llegara.
            foreach (var tenant in page.Where(candidate => candidate.OwnerType == OwnerType).GroupBy(candidate => candidate.TenantId))
            {
                released += await SweepTenantAsync(tenant.Key, tenant.ToList(), now, cancellationToken);
            }

            if (page.Count < BatchSize)
            {
                break;
            }
        }

        return released;
    }

    private async Task<int> SweepTenantAsync(
        Guid tenantId, IReadOnlyList<UnreferencedFileCandidate> candidates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlySet<Guid> referenced;
        try
        {
            referenced = await FindReferencedAsync(tenantId, candidates.Select(candidate => candidate.Id.Value).ToList(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogReferencesFailed(logger, exception, OwnerType, tenantId);
            return 0;
        }

        var released = 0;
        foreach (var candidate in candidates.Where(candidate => !referenced.Contains(candidate.Id.Value)))
        {
            if (await ReleaseAsync(tenantId, candidate.Id, now, cancellationToken))
            {
                released++;
            }
        }

        return released;
    }

    private async Task<bool> ReleaseAsync(Guid tenantId, FileResourceId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await releaser.ReleaseAsync(
                OwnerType,
                tenantId,
                id,
                Reason,
                async () => (await FindReferencedAsync(tenantId, [id.Value], cancellationToken)).Contains(id.Value),
                now,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFileFailed(logger, exception, OwnerType, id.Value);
            return false;
        }
    }
}
