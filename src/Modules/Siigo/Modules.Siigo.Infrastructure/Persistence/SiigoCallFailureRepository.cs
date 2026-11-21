using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>Sólo lectura y sin tracking (ver <see cref="ISiigoCallFailureRepository"/>). Todo filtra por <c>tenant_id</c>.</summary>
internal sealed class SiigoCallFailureRepository(SiigoDbContext dbContext) : ISiigoCallFailureRepository
{
    public async Task<(IReadOnlyList<SiigoCallFailure> Items, int Total)> SearchAsync(
        Guid tenantId,
        SiigoCallOperation? operation,
        int? status,
        DateTimeOffset? since,
        DateTimeOffset? until,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.CallFailures.AsNoTracking().Where(failure => failure.TenantId == tenantId);
        // Un filtro opcional se omite del texto cuando no viene: nunca se manda como parámetro nulo.
        if (operation is { } value)
        {
            query = query.Where(failure => failure.Operation == value);
        }

        if (status is { } code)
        {
            query = query.Where(failure => failure.Status == code);
        }

        // Npgsql rechaza un offset distinto de cero en un parámetro timestamptz: la hora local que manda la pantalla se pasa a UTC.
        if (since?.ToUniversalTime() is { } start)
        {
            query = query.Where(failure => failure.OccurredAt >= start);
        }

        if (until?.ToUniversalTime() is { } end)
        {
            query = query.Where(failure => failure.OccurredAt <= end);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(failure => failure.OccurredAt)
            .ThenByDescending(failure => failure.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<SiigoCallFailure?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await dbContext.CallFailures.AsNoTracking().FirstOrDefaultAsync(failure => failure.TenantId == tenantId && failure.Id == id, cancellationToken);
}
