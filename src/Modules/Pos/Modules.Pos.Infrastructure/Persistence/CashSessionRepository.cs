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
}
