using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-15 §3.1: el agregado con tracking (la versión es el token de concurrencia, como la respuesta rápida).</summary>
internal sealed class ResolutionReasonRepository(MessagingDbContext dbContext) : IResolutionReasonRepository, IResolutionReasonNames
{
    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);

    public Task<ResolutionReason?> FindActiveAsync(Guid tenantId, Guid reasonId, CancellationToken cancellationToken) =>
        dbContext.ResolutionReasons.SingleOrDefaultAsync(
            reason => reason.TenantId == tenantId && reason.Id == reasonId && reason.DeletedAt == null, cancellationToken);

    // lower() de los dos lados y en SQL: la misma expresión de IX_resolution_reasons_tenant_name. Bajarlo en .NET con ToLowerInvariant
    // podría discrepar de la collation de la base con letras como «Ó», y el resolver crearía un duplicado que el índice sí aceptaría.
    public Task<ResolutionReason?> FindActiveByNameAsync(Guid tenantId, string trimmedName, CancellationToken cancellationToken) =>
        dbContext.ResolutionReasons
            .FromSql($"SELECT * FROM messaging.resolution_reasons WHERE tenant_id = {tenantId} AND deleted_at IS NULL AND lower(name) = lower({trimmedName})")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

    public Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.ResolutionReasons.CountAsync(reason => reason.TenantId == tenantId && reason.DeletedAt == null, cancellationToken);

    public async Task<IReadOnlyList<ResolutionReason>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.ResolutionReasons.AsNoTracking()
            .Where(reason => reason.TenantId == tenantId && reason.DeletedAt == null)
            .ToListAsync(cancellationToken);
        // D-R10: el orden lo da .NET y no la collation de la base, que depende de cómo se creó el clúster. Son a lo sumo 100.
        return rows.OrderBy(reason => reason.Name, NameOrder).ThenBy(reason => reason.Id).ToArray();
    }

    public void Add(ResolutionReason reason) => dbContext.ResolutionReasons.Add(reason);

    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> reasonIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reasonIds);
        if (reasonIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        // Sin filtro de deleted_at (D-R5): el historial nombra también a los archivados.
        return await dbContext.ResolutionReasons.AsNoTracking()
            .Where(reason => reason.TenantId == tenantId && reasonIds.Contains(reason.Id))
            .ToDictionaryAsync(reason => reason.Id, reason => reason.Name, cancellationToken);
    }
}
