using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;
using Modules.Automations.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-29 §4.3 y D-A2: la ocupación neutral de las conexiones, <c>messaging.connection_bots</c> (dueña: Mensajería, migración
/// <c>AddConnectionBots</c>), al día con los chatbots publicados de <c>automations.chatbot_connections</c>. Vive en el composition root
/// por la misma razón que <see cref="CatalogVariantUsageChecker"/>: Automations no ve Mensajería, y el SQL entre esquemas se cablea acá.
///
/// **Corre sobre el <see cref="AutomationsDbContext"/> de la petición**, dentro de la transacción que abrió
/// <c>AutomationsUnitOfWork</c>: la fila de Mensajería cambia en el mismo commit que el chatbot. La clave primaria de
/// <c>connection_bots</c> es la conexión, así que dos bots que la quieren a la vez se ordenan solos: el segundo espera al primero y ve
/// la fila tomada.
///
/// **Acoplamiento por nombre** con <c>automations.chatbots</c>, <c>automations.chatbot_connections</c> y
/// <c>messaging.connection_bots</c>: renombrar una de esas columnas compila y deja publicar, pausar, editar y borrar chatbots en 500
/// (<c>42703</c>). Lo amarran las pruebas de ocupación de <c>ChatbotsApiTests</c>, contra el esquema migrado.
/// </summary>
internal sealed class ChatbotConnectionSlots(AutomationsDbContext automations) : IChatbotConnectionSlots
{
    public async Task<IReadOnlyList<ConnectionOccupant>> SyncChatbotsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Libera lo que ningún chatbot activo del tenant tiene ya (despublicado, borrado, conexión quitada o tomada por otro chatbot).
        await automations.Database.ExecuteSqlAsync($"""
            DELETE FROM messaging.connection_bots b
            WHERE b.tenant_id = {tenantId} AND b.bot_kind = 1
              AND NOT EXISTS (
                  SELECT 1 FROM automations.chatbot_connections cc
                  WHERE cc.chatbot_id = b.bot_id AND cc.connection_id = b.connection_id AND cc.active)
            """, cancellationToken);

        // Toma las nuevas y renueva el nombre de las propias. Una conexión que ocupa otro bot (un agente) no se pisa: queda en la
        // consulta de abajo y el guardado se deshace con 409.
        await automations.Database.ExecuteSqlAsync($"""
            INSERT INTO messaging.connection_bots (connection_id, tenant_id, bot_kind, bot_id, bot_name, reserved_at)
            SELECT cc.connection_id, cc.tenant_id, 1, c.id, c.name, now()
            FROM automations.chatbot_connections cc
            JOIN automations.chatbots c ON c.id = cc.chatbot_id
            WHERE cc.tenant_id = {tenantId} AND cc.active
            ON CONFLICT (connection_id) DO UPDATE SET bot_name = EXCLUDED.bot_name
            WHERE messaging.connection_bots.bot_kind = 1 AND messaging.connection_bots.bot_id = EXCLUDED.bot_id
            """, cancellationToken);

        var taken = await automations.Database.SqlQuery<TakenRow>($"""
            SELECT b.connection_id AS "ConnectionId", b.bot_kind AS "BotKind", b.bot_id AS "BotId", b.bot_name AS "BotName"
            FROM automations.chatbot_connections cc
            JOIN messaging.connection_bots b ON b.connection_id = cc.connection_id
            WHERE cc.tenant_id = {tenantId} AND cc.active AND NOT (b.bot_kind = 1 AND b.bot_id = cc.chatbot_id)
            ORDER BY b.connection_id
            """).ToListAsync(cancellationToken);

        return [.. taken.Select(row => new ConnectionOccupant(
            row.ConnectionId, row.BotKind == 2 ? ConnectionOccupant.Agent : ConnectionOccupant.Chatbot, row.BotId, row.BotName))];
    }

    public async Task<IReadOnlyList<ConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await automations.Database.SqlQuery<TakenRow>($"""
            SELECT connection_id AS "ConnectionId", bot_kind AS "BotKind", bot_id AS "BotId", bot_name AS "BotName"
            FROM messaging.connection_bots
            WHERE tenant_id = {tenantId}
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new ConnectionOccupant(
            row.ConnectionId, row.BotKind == 2 ? ConnectionOccupant.Agent : ConnectionOccupant.Chatbot, row.BotId, row.BotName))];
    }

    private sealed record TakenRow(Guid ConnectionId, short BotKind, Guid BotId, string BotName);
}
