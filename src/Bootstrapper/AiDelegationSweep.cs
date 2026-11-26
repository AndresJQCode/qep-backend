using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Messaging.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 §6.5: una delegación <c>Live</c> cuya conversación ya no la apunta (o ya no existe) no la retoma nadie: pasa a
/// <c>Interrupted</c>. Hoy lo dejan así un traspaso o un fin de la sesión que limpia la columna sin cerrar la fila (el gotcha de B1), un
/// borrado de cliente a medias o un rollback manual. Es una sola sentencia: dos pods que barren a la vez no se pisan.
/// <para>
/// Revisión de la rama, hallazgo 3: la corrida que delegó espera en el bloque y el barrido de abandonadas la salta (§6.5), así que cada
/// delegación interrumpida deja en la misma sentencia una señal <c>ReleaseRun</c> para el motor de chatbots (<c>bot_kind = 1</c>), como la
/// toma humana (<c>ReleaseRunSignal</c>). Sin conversación no hay corrida que esperar ni fila que encolar.
/// </para>
/// <para>
/// <b>Acoplamiento por nombre</b>: escribe <c>messaging.bot_delegations</c> (<c>status</c>, <c>ended_at</c>) y
/// <c>messaging.automation_inbox</c> (la fila de <c>AutomationInbox.NewReleaseRun</c>), y lee
/// <c>messaging.conversations.automation_delegation_id</c> con SQL. <c>DelegateToAgent</c> escribe las dos primeras en la misma transacción,
/// así que una delegación recién creada nunca se ve huérfana.
/// </para>
/// </summary>
internal sealed class AiDelegationSweep(MessagingDbContext messaging) : IAiDelegationSweep
{
    public async Task<int> InterruptOrphansAsync(DateTimeOffset now, int batch, CancellationToken cancellationToken)
    {
        // Npgsql sólo escribe un timestamptz desde un DateTimeOffset en UTC.
        var at = now.ToUniversalTime();
        var rows = await messaging.Database.SqlQuery<int>(
            $"""
            WITH interrupted AS (
                UPDATE messaging.bot_delegations
                   SET status = 'Interrupted', ended_at = {at}
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
                RETURNING tenant_id, connection_id, conversation_id),
            signaled AS (
                INSERT INTO messaging.automation_inbox
                       (message_id, tenant_id, connection_id, conversation_id, opened, created_at, attempts, next_attempt_at, kind, bot_kind)
                SELECT gen_random_uuid(), i.tenant_id, i.connection_id, i.conversation_id, false, {at}, 0, {at}, 'ReleaseRun', 1
                  FROM interrupted i
                 WHERE EXISTS (SELECT 1 FROM messaging.conversations c WHERE c.id = i.conversation_id AND c.tenant_id = i.tenant_id)
                RETURNING 1)
            SELECT count(*)::int AS "Value" FROM interrupted
            """).ToListAsync(cancellationToken);
        return rows[0];
    }
}
