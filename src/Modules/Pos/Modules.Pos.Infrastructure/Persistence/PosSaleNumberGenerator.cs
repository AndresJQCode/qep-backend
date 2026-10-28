using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>Copia de OrderNumberGenerator sin la columna year: UPDATE ... RETURNING atómico sobre una fila por tenant.</summary>
internal sealed class PosSaleNumberGenerator(PosDbContext dbContext) : IPosSaleNumberGenerator
{
    public async Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO pos.sale_number_counters (tenant_id, next_value)
            VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE pos.sale_number_counters
                SET next_value = next_value + 1
                WHERE tenant_id = {tenantId}
                RETURNING next_value - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The POS sale number counter for tenant '{tenantId}' could not be read back.");
    }
}
