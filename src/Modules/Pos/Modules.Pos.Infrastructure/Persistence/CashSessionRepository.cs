using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

internal sealed class CashSessionRepository(PosDbContext dbContext) : ICashSessionRepository
{
    public Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken cancellationToken) =>
        dbContext.CashSessions.SingleOrDefaultAsync(
            session => session.TenantId == tenantId && session.Id == id, cancellationToken);

    public Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken cancellationToken) =>
        dbContext.CashSessions.SingleOrDefaultAsync(
            session => session.TenantId == tenantId
                && session.CashierId == cashier
                && session.Status == CashSessionStatus.Open,
            cancellationToken);

    public void Add(CashSession session) => dbContext.CashSessions.Add(session);

    public async Task<(IReadOnlyList<CashSession> Items, int Total)> ListAsync(
        CashSessionFilter filter, CancellationToken cancellationToken)
    {
        var query = dbContext.CashSessions.AsNoTracking().Where(session => session.TenantId == filter.TenantId);
        if (filter.Cashier is { } cashier)
        {
            query = query.Where(session => session.CashierId == cashier);
        }

        if (filter.OpenedFromUtc is { } from)
        {
            query = query.Where(session => session.OpenedAt >= from);
        }

        if (filter.OpenedToUtc is { } to)
        {
            query = query.Where(session => session.OpenedAt < to);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(session => session.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(session => session.OpenedAt)
            .ThenByDescending(session => session.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }
}
