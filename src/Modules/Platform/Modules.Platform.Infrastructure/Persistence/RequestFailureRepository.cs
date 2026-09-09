using Microsoft.EntityFrameworkCore;
using Modules.Platform.Application;
using Modules.Platform.Domain;

namespace Modules.Platform.Infrastructure.Persistence;

internal sealed class RequestFailureRepository(PlatformDbContext dbContext)
    : IRequestFailureRepository
{
    public async Task<(IReadOnlyList<RequestFailure> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? moduleName,
        string? errorCode,
        string? traceId,
        DateTimeOffset? occurredFrom,
        DateTimeOffset? occurredTo,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = Scoped(tenantId);

        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            query = query.Where(failure => failure.Module == moduleName);
        }

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            query = query.Where(failure => failure.ErrorCode == errorCode);
        }

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            query = query.Where(failure => failure.TraceId == traceId);
        }

        if (occurredFrom is { } from)
        {
            query = query.Where(failure => failure.OccurredAt >= from);
        }

        if (occurredTo is { } to)
        {
            query = query.Where(failure => failure.OccurredAt <= to);
        }

        var total = await query.CountAsync(cancellationToken);

        // Por id como desempate: dos fallas del mismo request masivo comparten el instante, y sin
        // un segundo criterio el orden entre paginas no esta garantizado. RequestFailureId es un
        // GUID v7, asi que ordena por tiempo.
        var items = await query
            .OrderByDescending(failure => failure.OccurredAt)
            .ThenByDescending(failure => failure.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<(IReadOnlyList<string> Modules, IReadOnlyList<string> ErrorCodes)>
        ListFilterValuesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Dos consultas de valores distintos y no una pagina de filas: lo que se necesita son los
        // valores presentes, y traerlos con Distinct en la base evita bajar la tabla entera para
        // deduplicar en memoria.
        var modules = await Scoped(tenantId)
            .Select(failure => failure.Module)
            .Distinct()
            .OrderBy(module => module)
            .ToListAsync(cancellationToken);

        var errorCodes = await Scoped(tenantId)
            .Where(failure => failure.ErrorCode != null)
            .Select(failure => failure.ErrorCode!)
            .Distinct()
            .OrderBy(code => code)
            .ToListAsync(cancellationToken);

        return (modules, errorCodes);
    }

    public Task<int> PurgeOlderThanAsync(
        Guid tenantId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken) =>
        // ExecuteDeleteAsync y no cargar-y-borrar: el purgado toca miles de filas, y materializar
        // cada una para que EF genere su DELETE seria bajar el log entero para tirarlo. Va directo
        // como un solo DELETE, y por eso tampoco pasa por una unidad de trabajo.
        Scoped(tenantId)
            .Where(failure => failure.OccurredAt < olderThan)
            .ExecuteDeleteAsync(cancellationToken);

    // El filtro de tenant, en un solo lugar: es la garantia que no se puede olvidar en ninguna de
    // las tres consultas. AsNoTracking porque las filas son de solo lectura una vez escritas.
    private IQueryable<RequestFailure> Scoped(Guid tenantId) =>
        dbContext.RequestFailures
            .AsNoTracking()
            .Where(failure => failure.TenantId == tenantId);
}
