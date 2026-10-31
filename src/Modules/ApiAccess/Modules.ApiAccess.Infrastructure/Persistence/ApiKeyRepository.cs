using Microsoft.EntityFrameworkCore;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.Infrastructure.Persistence;

internal sealed class ApiKeyRepository(ApiAccessDbContext dbContext) : IApiKeyRepository
{
    // El último uso se escribe a lo sumo una vez por minuto: una clave muy activa no convierte
    // cada request en una escritura.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public void Add(ApiKey key) => dbContext.ApiKeys.Add(key);

    public Task<ApiKey?> FindAsync(Guid tenantId, ApiKeyId id, CancellationToken cancellationToken) =>
        dbContext.ApiKeys.FirstOrDefaultAsync(
            key => key.TenantId == tenantId && key.Id == id,
            cancellationToken);

    public async Task<IReadOnlyList<ApiKey>> ListAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.ApiKeys
            .AsNoTracking()
            .Where(key => key.TenantId == tenantId)
            .OrderByDescending(key => key.CreatedAt)
            .ThenByDescending(key => key.Id)
            .ToListAsync(cancellationToken);

    public Task<int> CountActiveAsync(
        Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.ApiKeys.CountAsync(
            key => key.TenantId == tenantId
                && key.RevokedAt == null
                && (key.ExpiresAt == null || key.ExpiresAt > now),
            cancellationToken);

    public Task<ApiKey?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
        dbContext.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(key => key.KeyPrefix == keyPrefix, cancellationToken);

    public async Task TouchLastUsedAsync(
        ApiKeyId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var threshold = now - TouchInterval;
        await dbContext.ApiKeys
            .Where(key => key.Id == id && (key.LastUsedAt == null || key.LastUsedAt < threshold))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(key => key.LastUsedAt, now),
                cancellationToken);
    }
}
