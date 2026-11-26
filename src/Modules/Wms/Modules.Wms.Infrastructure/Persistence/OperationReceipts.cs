using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class OperationReceipts(WmsDbContext dbContext, IdempotencyScope scope) : IOperationReceipts
{
    public Task<OperationReceipt?> FindAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        dbContext.Set<OperationReceipt>()
            .AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.TenantId == tenantId && receipt.IdempotencyKey == key, cancellationToken);

    public void Record<TResponse>(TResponse response)
    {
        if (!scope.IsActive)
        {
            throw new InvalidOperationException(
                "The idempotency scope is not active: an idempotent route must run behind IdempotencyKeyFilter.");
        }

        dbContext.Set<OperationReceipt>().Add(OperationReceipt.Create(
            scope.TenantId,
            scope.Key!,
            scope.RequestHash!,
            JsonSerializer.Serialize(response, JsonSerializerOptions.Web),
            DateTimeOffset.UtcNow));
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        dbContext.Set<OperationReceipt>().Where(receipt => receipt.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
}
