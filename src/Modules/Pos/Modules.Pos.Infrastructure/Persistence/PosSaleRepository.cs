using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

internal sealed class PosSaleRepository(PosDbContext dbContext) : IPosSaleRepository
{
    public Task<PosSale?> FindAsync(Guid tenantId, PosSaleId id, CancellationToken cancellationToken) =>
        dbContext.Sales.SingleOrDefaultAsync(
            sale => sale.TenantId == tenantId && sale.Id == id, cancellationToken);

    public void Add(PosSale sale) => dbContext.Sales.Add(sale);

    public async Task<(IReadOnlyList<PosSaleListRow> Items, int Total)> ListAsync(
        PosSaleFilter filter, CancellationToken cancellationToken)
    {
        var query = dbContext.Sales.AsNoTracking().IgnoreAutoIncludes().Where(sale => sale.TenantId == filter.TenantId);
        if (filter.Cashier is { } cashier)
        {
            query = query.Where(sale => sale.CashierId == cashier);
        }

        if (filter.SessionId is { } sessionId)
        {
            query = query.Where(sale => sale.CashSessionId == sessionId);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(sale => sale.CreatedAt >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(sale => sale.CreatedAt < to);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(sale => sale.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Number))
        {
            var number = filter.Number.Trim();
            query = query.Where(sale => sale.SaleNumber == number);
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query
            .OrderByDescending(sale => sale.CreatedAt)
            .ThenByDescending(sale => sale.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Include(sale => sale.Payments)
            .ToListAsync(cancellationToken);

        // Una consulta por página, no una por fila: el estado de la caja decide voidable.
        var sessionIds = page.Select(sale => sale.CashSessionId).Distinct().ToList();
        var sessions = await dbContext.CashSessions
            .AsNoTracking()
            .Where(session => session.TenantId == filter.TenantId && sessionIds.Contains(session.Id))
            .Select(session => new { session.Id, session.CashierName, session.Status })
            .ToDictionaryAsync(session => session.Id, cancellationToken);

        return (page
            .Select(sale => new PosSaleListRow(
                sale, sessions[sale.CashSessionId].CashierName, sessions[sale.CashSessionId].Status))
            .ToList(), total);
    }
}
