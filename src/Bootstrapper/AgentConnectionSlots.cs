using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-29 §4.3 y D-A2: la ocupación neutral de las conexiones, <c>messaging.connection_bots</c> (dueña: Mensajería), al día con
/// los agentes publicados de <c>ai.agent_connections</c>. Gemela de <see cref="ChatbotConnectionSlots"/>, con <c>bot_kind = 2</c>: Ai no ve
/// Mensajería, y el SQL entre esquemas se cablea acá.
///
/// **Corre sobre el <see cref="AiDbContext"/> de la petición**, dentro de la transacción que abrió <c>AiUnitOfWork</c>: la fila de
/// Mensajería cambia en el mismo commit que el agente. La clave primaria de <c>connection_bots</c> es la conexión, así que un chatbot y un
/// agente que la quieren a la vez se ordenan: el candado del tenant (<see cref="ConnectionSlotsLock"/>) hace esperar al segundo, que ve
/// la fila tomada.
///
/// **Acoplamiento por nombre** con <c>ai.agents</c>, <c>ai.agent_connections</c> y <c>messaging.connection_bots</c>: renombrar una de esas
/// columnas compila y deja publicar, pausar, editar y borrar agentes en 500 (<c>42703</c>). Lo amarran las pruebas de
/// <c>AgentsApiTests</c>, contra el esquema migrado.
/// </summary>
internal sealed class AgentConnectionSlots(AiDbContext ai) : IAgentConnectionSlots
{
    public async Task<IReadOnlyList<AiConnectionOccupant>> SyncAgentsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Antes de tocar una fila: dos sincronizaciones del mismo tenant (también la de un chatbot) hacen fila acá en vez de cruzarse
        // en un deadlock entre el DELETE y el INSERT de abajo (ConnectionSlotsLock).
        await ConnectionSlotsLock.AcquireAsync(ai, tenantId, cancellationToken);

        // Libera lo que ningún agente publicado del tenant tiene ya (borrado, conexión quitada). Un borrador no ocupa (D-A10).
        await ai.Database.ExecuteSqlAsync($"""
            DELETE FROM messaging.connection_bots b
            WHERE b.tenant_id = {tenantId} AND b.bot_kind = 2
              AND NOT EXISTS (
                  SELECT 1 FROM ai.agent_connections ac
                  JOIN ai.agents a ON a.id = ac.agent_id
                  WHERE ac.agent_id = b.bot_id AND ac.connection_id = b.connection_id AND a.status <> 'Draft')
            """, cancellationToken);

        // Toma las nuevas y renueva el nombre de las propias. Una conexión que ocupa otro bot no se pisa: queda en la consulta de abajo
        // y el guardado se deshace con 409. Lo que evita el deadlock es el candado de arriba; el ORDER BY sólo deja el orden fijo.
        await ai.Database.ExecuteSqlAsync($"""
            INSERT INTO messaging.connection_bots (connection_id, tenant_id, bot_kind, bot_id, bot_name, reserved_at)
            SELECT ac.connection_id, ac.tenant_id, 2, a.id, a.name, now()
            FROM ai.agent_connections ac
            JOIN ai.agents a ON a.id = ac.agent_id
            WHERE ac.tenant_id = {tenantId} AND a.status <> 'Draft'
            ORDER BY ac.connection_id
            ON CONFLICT (connection_id) DO UPDATE SET bot_name = EXCLUDED.bot_name
            WHERE messaging.connection_bots.bot_kind = 2 AND messaging.connection_bots.bot_id = EXCLUDED.bot_id
            """, cancellationToken);

        var taken = await ai.Database.SqlQuery<SlotRow>($"""
            SELECT b.connection_id AS "ConnectionId", b.bot_kind AS "BotKind", b.bot_id AS "BotId", b.bot_name AS "BotName"
            FROM ai.agent_connections ac
            JOIN ai.agents a ON a.id = ac.agent_id
            JOIN messaging.connection_bots b ON b.connection_id = ac.connection_id
            WHERE ac.tenant_id = {tenantId} AND a.status <> 'Draft' AND NOT (b.bot_kind = 2 AND b.bot_id = ac.agent_id)
            ORDER BY b.connection_id
            """).ToListAsync(cancellationToken);
        return [.. taken.Select(ToOccupant)];
    }

    public async Task<IReadOnlyList<AiConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await ai.Database.SqlQuery<SlotRow>($"""
            SELECT connection_id AS "ConnectionId", bot_kind AS "BotKind", bot_id AS "BotId", bot_name AS "BotName"
            FROM messaging.connection_bots
            WHERE tenant_id = {tenantId}
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(ToOccupant)];
    }

    private static AiConnectionOccupant ToOccupant(SlotRow row) =>
        new(row.ConnectionId, row.BotKind == 2 ? AiConnectionOccupant.Agent : AiConnectionOccupant.Chatbot, row.BotId, row.BotName);

    private sealed record SlotRow(Guid ConnectionId, short BotKind, Guid BotId, string BotName);
}
