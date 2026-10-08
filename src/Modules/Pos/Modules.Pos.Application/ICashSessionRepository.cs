using Modules.Pos.Domain;

namespace Modules.Pos.Application;

/// <summary>Todo método recibe tenantId: el id de una caja de otro tenant responde igual que uno inexistente.</summary>
public interface ICashSessionRepository
{
    Task<CashSession?> FindAsync(Guid tenantId, CashSessionId id, CancellationToken cancellationToken);

    Task<CashSession?> FindOpenByCashierAsync(Guid tenantId, MemberId cashier, CancellationToken cancellationToken);

    void Add(CashSession session);

    /// <summary>Por apertura descendente.</summary>
    Task<(IReadOnlyList<CashSession> Items, int Total)> ListAsync(
        CashSessionFilter filter, CancellationToken cancellationToken);
}
