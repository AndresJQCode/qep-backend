using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Emite el próximo consecutivo con un <c>UPDATE ... RETURNING</c> atómico, mismo mecanismo que
/// <c>CucGenerator</c> en Customers — <c>INSERT ... ON CONFLICT DO NOTHING</c> asegura la fila del
/// (tenant, año), y el <c>UPDATE</c> toma su lock para serializar la emisión entre altas
/// concurrentes. Corre en la conexión del <see cref="QuotationsDbContext"/>, así que participa de
/// la misma transacción que abre <c>SaveChangesAsync</c>.
/// </summary>
internal sealed class QuotationNumberGenerator(QuotationsDbContext dbContext)
    : IQuotationNumberGenerator
{
    public async Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.quotation_number_counters (tenant_id, year, next_value)
            VALUES ({tenantId}, {year}, 1)
            ON CONFLICT (tenant_id, year) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE quotations.quotation_number_counters
                SET next_value = next_value + 1
                WHERE tenant_id = {tenantId} AND year = {year}
                RETURNING next_value - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        // La fila existe: la acaba de asegurar el INSERT de arriba, en la misma conexión.
        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The quotation number counter for tenant '{tenantId}' year {year} " +
                "could not be read back.");
    }
}
