using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>Mismo mecanismo que <see cref="QuotationNumberGenerator"/>: <c>UPDATE ... RETURNING</c>
/// atómico sobre una fila por (tenant, año).</summary>
internal sealed class SaleNumberGenerator(QuotationsDbContext dbContext) : ISaleNumberGenerator
{
    public async Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.sale_number_counters (tenant_id, year, next_value)
            VALUES ({tenantId}, {year}, 1)
            ON CONFLICT (tenant_id, year) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE quotations.sale_number_counters
                SET next_value = next_value + 1
                WHERE tenant_id = {tenantId} AND year = {year}
                RETURNING next_value - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The sale number counter for tenant '{tenantId}' year {year} could not be read back.");
    }
}
