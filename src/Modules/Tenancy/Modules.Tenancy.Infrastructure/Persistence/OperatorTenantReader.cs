using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Spec 2026-10-08 §5: la lectura de la consola, sin seguimiento de cambios.</summary>
internal sealed class OperatorTenantReader(TenancyDbContext dbContext) : IOperatorTenantReader
{
    private const string LikeEscapeCharacter = "\\";

    public async Task<OperatorTenantListing> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var tenants = dbContext.Tenants.AsNoTracking();
        var pattern = LikePattern(search);
        var filtered = pattern is null
            ? tenants
            : tenants.Where(tenant =>
                EF.Functions.ILike(tenant.DisplayName, pattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(tenant.Slug, pattern, LikeEscapeCharacter));

        var total = await filtered.CountAsync(cancellationToken);
        var rows = await filtered
            .OrderBy(tenant => tenant.DisplayName)
            .ThenBy(tenant => tenant.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(tenant => new OperatorTenantRow(
                tenant.Id.Value, tenant.Slug, tenant.DisplayName, tenant.Status, tenant.CreatedAt,
                dbContext.TenantModules.Count(module =>
                    module.TenantId == tenant.Id && module.Status == TenantModuleStatus.Active)))
            .ToListAsync(cancellationToken);

        // El resumen cuenta todos los tenants, sin la búsqueda (§5).
        var allTenants = await tenants.CountAsync(cancellationToken);
        var withoutModules = await tenants.CountAsync(
            tenant => !dbContext.TenantModules.Any(module =>
                module.TenantId == tenant.Id && module.Status == TenantModuleStatus.Active),
            cancellationToken);
        var inactive = await tenants.CountAsync(tenant => tenant.Status != TenantStatus.Active, cancellationToken);

        return new OperatorTenantListing(rows, total, allTenants, withoutModules, inactive);
    }

    public async Task<OperatorTenantSnapshot?> FindAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.AsNoTracking()
            .Where(value => value.Id == tenantId)
            .Select(value => new { value.Slug, value.DisplayName, value.CreatedAt, value.Status, value.Version })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var modules = await dbContext.TenantModules.AsNoTracking()
            .Where(module => module.TenantId == tenantId)
            .Select(module => new TenantModuleState(module.ModuleKey, module.Status, module.StatusChangedAt, module.Source))
            .ToListAsync(cancellationToken);

        // El último motivo por módulo se elige en memoria: el historial de un tenant es corto, y un
        // "último por grupo" en SQL no se justifica hoy.
        var moduleChanges = await dbContext.TenantChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId && change.Kind == TenantChangeKind.Module)
            .OrderByDescending(change => change.OccurredAt)
            .Select(change => new { change.ModuleKey, change.Reason })
            .ToListAsync(cancellationToken);
        var lastReasons = moduleChanges
            .Where(change => change.ModuleKey is not null)
            .DistinctBy(change => change.ModuleKey!)
            .ToDictionary(change => change.ModuleKey!, change => change.Reason);

        var lastStatus = await dbContext.TenantChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId && change.Kind == TenantChangeKind.TenantStatus)
            .OrderByDescending(change => change.OccurredAt)
            .Select(change => new TenantStatusChange(change.OccurredAt, change.Reason))
            .FirstOrDefaultAsync(cancellationToken);

        return new OperatorTenantSnapshot(
            tenantId.Value, tenant.Slug, tenant.DisplayName, tenant.CreatedAt, tenant.Status, tenant.Version,
            modules, lastReasons, lastStatus);
    }

    public async Task<TenantChangeBatchPage> ListHistoryAsync(
        TenantId tenantId, TenantModuleKey? moduleKey, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = dbContext.TenantChanges.AsNoTracking().Where(change => change.TenantId == tenantId);
        // §5: con el filtro, los lotes que tocan ese módulo (con todos sus cambios) y ninguno de estado.
        var scoped = moduleKey is null
            ? rows
            : rows.Where(change => change.Kind == TenantChangeKind.Module && change.ModuleKey == moduleKey);
        var batches = scoped
            .GroupBy(change => change.BatchId)
            .Select(group => new { BatchId = group.Key, OccurredAt = group.Max(change => change.OccurredAt) });

        var total = await batches.CountAsync(cancellationToken);
        // Desempate por BatchId (UUID v7, crece con el tiempo): dos lotes en el mismo instante salen
        // siempre en el mismo orden y la paginación no repite ni salta ninguno.
        var pageIds = await batches
            .OrderByDescending(batch => batch.OccurredAt)
            .ThenByDescending(batch => batch.BatchId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(batch => batch.BatchId)
            .ToListAsync(cancellationToken);
        if (pageIds.Count == 0)
        {
            return new TenantChangeBatchPage([], total);
        }

        // Sin el filtro de módulo: el lote viaja completo.
        var changes = await rows.Where(change => pageIds.Contains(change.BatchId)).ToListAsync(cancellationToken);
        var byBatch = changes.ToLookup(change => change.BatchId);
        return new TenantChangeBatchPage(
            pageIds
                .Select(id => (IReadOnlyList<TenantChange>)byBatch[id]
                    .OrderBy(change => change.ModuleKey is null ? -1 : IndexOf(change.ModuleKey))
                    .ThenBy(change => change.Id)
                    .ToArray())
                .ToArray(),
            total);
    }

    private static int IndexOf(TenantModuleKey key)
    {
        for (var index = 0; index < TenantModuleKeys.All.Count; index++)
        {
            if (TenantModuleKeys.All[index] == key)
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    // Mismo escape que ProductRepository: la barra va primero.
    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : "%" + trimmed
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
    }
}
