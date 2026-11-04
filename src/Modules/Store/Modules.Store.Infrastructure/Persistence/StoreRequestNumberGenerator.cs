using Microsoft.EntityFrameworkCore;
using Modules.Store.Application;

namespace Modules.Store.Infrastructure.Persistence;

/// <summary>
/// Mismo mecanismo que <c>OrderNumberGenerator</c>: <c>UPDATE ... RETURNING</c> atómico sobre una fila por tenant, así que dos
/// solicitudes simultáneas nunca reciben el mismo número.
/// </summary>
internal sealed class StoreRequestNumberGenerator(StoreDbContext dbContext) : IStoreRequestNumberGenerator
{
    public async Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO store.request_counters (tenant_id, next_value)
            VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE store.request_counters
                SET next_value = next_value + 1
                WHERE tenant_id = {tenantId}
                RETURNING next_value - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The request number counter for tenant '{tenantId}' could not be read back.");
    }
}
