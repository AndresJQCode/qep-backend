using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-11-12 (correcciones de la revisión), Tarea 3: quién atiende una conexión antes de borrarla, leído de
/// <c>messaging.connection_bots</c> (spec 2026-10-29 §4.3). Vive acá porque Integrations no ve Mensajería (<c>IntegrationsLayerTests</c>).
///
/// **Acoplamiento por nombre** con <c>messaging.connection_bots</c> (<c>tenant_id</c>, <c>connection_id</c>, <c>bot_kind</c>,
/// <c>bot_id</c>, <c>bot_name</c>): renombrar una de esas columnas compila y deja el <c>DELETE</c> de toda conexión en 500 (<c>42703</c>).
/// Lo amarran <c>AgentsApiTests.DeletingAConnectionAnAgentServesIsRejected</c>,
/// <c>AgentsApiTests.DeletingAConnectionAPausedAgentServesIsRejected</c> y
/// <c>ChatbotsApiTests.DeletingAConnectionAChatbotServesIsRejected</c>, contra el esquema migrado.
/// </summary>
internal sealed class IntegrationsConnectionBotOccupancy(IntegrationsDbContext integrations) : IConnectionBotOccupancy
{
    public async Task<ConnectionBotOccupant?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var rows = await integrations.Database.SqlQuery<OccupantRow>($"""
            SELECT bot_kind AS "BotKind", bot_id AS "BotId", bot_name AS "BotName"
            FROM messaging.connection_bots
            WHERE tenant_id = {tenantId} AND connection_id = {connectionId}
            FOR SHARE
            """).ToListAsync(cancellationToken);
        return rows is [var row]
            ? new ConnectionBotOccupant(row.BotKind == 2 ? ConnectionBotOccupant.Agent : ConnectionBotOccupant.Chatbot, row.BotId, row.BotName)
            : null;
    }

    private sealed record OccupantRow(short BotKind, Guid BotId, string BotName);
}
