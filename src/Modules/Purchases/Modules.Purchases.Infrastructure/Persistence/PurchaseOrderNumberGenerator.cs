using Microsoft.EntityFrameworkCore;
using Modules.Purchases.Application;

namespace Modules.Purchases.Infrastructure.Persistence;

/// <summary>
/// Copia de <c>PosSaleNumberGenerator</c>: <c>UPDATE … RETURNING</c> atómico sobre una fila por tenant, que se crea la primera vez.
/// El <c>UPDATE</c> toma el candado de la fila, así que dos altas a la vez se serializan y la segunda lee el valor ya incrementado.
/// Corre en la transacción del alta (guarda de <c>SupportTicketNumberGenerator</c>): si el alta falla, el número vuelve. Borrar un
/// borrador sí deja un hueco (D-10).
/// </summary>
internal sealed class PurchaseOrderNumberGenerator(PurchasesDbContext dbContext) : IPurchaseOrderNumberGenerator
{
    public async Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The purchase order number needs the transaction of the order creation (IPurchasesUnitOfWork.BeginTransactionAsync).");
        }

        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO purchases.order_number_counters (tenant_id, next_value)
            VALUES ({tenantId}, 1)
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            cancellationToken);

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE purchases.order_number_counters
                SET next_value = next_value + 1
                WHERE tenant_id = {tenantId}
                RETURNING next_value - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The purchase order number counter for tenant '{tenantId}' could not be read back.");
    }
}
