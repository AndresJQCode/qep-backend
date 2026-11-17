using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Messaging.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 §6.5: una delegación <c>Live</c> cuya conversación ya no la apunta (o ya no existe) no la retoma nadie: pasa a
/// <c>Interrupted</c>. Hoy lo dejan así un traspaso o un fin de la sesión que limpia la columna sin cerrar la fila (el gotcha de B1), un
/// borrado de cliente a medias o un rollback manual. Es un solo <c>UPDATE</c>: dos pods que barren a la vez no se pisan.
/// <para>
/// <b>Acoplamiento por nombre</b>: escribe <c>messaging.bot_delegations</c> (<c>status</c>, <c>ended_at</c>) y lee
/// <c>messaging.conversations.automation_delegation_id</c> con SQL. <c>DelegateToAgent</c> escribe las dos en la misma transacción, así que
/// una delegación recién creada nunca se ve huérfana.
/// </para>
/// </summary>
internal sealed class AiDelegationSweep(MessagingDbContext messaging) : IAiDelegationSweep
{
    public Task<int> InterruptOrphansAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken) =>
        messaging.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.bot_delegations
               SET status = 'Interrupted', ended_at = {now}
             WHERE id IN (
                   SELECT d.id
                     FROM messaging.bot_delegations d
                    WHERE d.status = 'Live'
                      AND NOT EXISTS (
                          SELECT 1 FROM messaging.conversations c
                           WHERE c.id = d.conversation_id AND c.automation_delegation_id = d.id)
                    ORDER BY d.started_at
                    LIMIT {batch}
                      FOR UPDATE SKIP LOCKED)
               AND status = 'Live'
            """,
            cancellationToken);
}
