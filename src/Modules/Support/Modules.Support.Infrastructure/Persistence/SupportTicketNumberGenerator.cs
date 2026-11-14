using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Plan D2 (precedente <c>CucGenerator</c>): el radicado sale de un <c>UPDATE … RETURNING</c> sobre la fila del tenant. El
/// <c>UPDATE</c> toma el lock de fila, así que dos altas a la vez se serializan y la segunda lee el valor ya incrementado. Corre en
/// la transacción del alta: si el alta falla, el número vuelve (a diferencia del CUC, el radicado no deja huecos). No toca
/// <c>version</c>: guardar el umbral de «por vencer» nunca choca con un alta.
/// </summary>
internal sealed class SupportTicketNumberGenerator(SupportDbContext dbContext) : ISupportTicketNumberGenerator
{
    public async Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The support ticket number needs the transaction of the ticket creation (ISupportUnitOfWork.BeginTransactionAsync).");
        }

        var emitted = await dbContext.Database
            .SqlQuery<long>(
                $"""
                UPDATE support.tenant_settings
                SET next_ticket_number = next_ticket_number + 1
                WHERE tenant_id = {tenantId}
                RETURNING next_ticket_number - 1 AS "Value"
                """)
            .ToListAsync(cancellationToken);

        // Sin fila, el tenant no pasó por ISupportProvisioner: emitir un número inventado sería peor que fallar.
        return emitted.Count == 1
            ? emitted[0]
            : throw new InvalidOperationException(
                $"The support settings of tenant '{tenantId}' do not exist; call ISupportProvisioner.EnsureAsync first.");
    }
}
