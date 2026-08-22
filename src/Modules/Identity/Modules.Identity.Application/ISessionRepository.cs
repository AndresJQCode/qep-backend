using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public interface ISessionRepository
{
    Task<Session?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<Session>> ListActiveByUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    void Add(Session session);
}
