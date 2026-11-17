using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Automations.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 §7 y D-B18: los chatbots publicados que usan cada agente, para la lista de agentes y el 409 al borrar. Ai no ve Automations
/// (D-A3), así que la lectura va acá.
/// <para>
/// <b>Acoplamiento por nombre</b>: lee <c>automations.chatbots</c> (<c>id</c>, <c>tenant_id</c>, <c>name</c>, <c>published_version_id</c>) y
/// <c>automations.chatbot_versions.flow</c> (<c>nodes[].kind = 'aiAgent'</c> y <c>nodes[].agentId</c>, el JSON de <c>FlowJson</c>) con SQL.
/// Renombrar una de esas columnas o propiedades compila y deja borrar un agente que un chatbot usa. Sólo cuenta la versión publicada: el
/// borrador no corre.
/// </para>
/// </summary>
internal sealed class AiChatbotUsage(AutomationsDbContext automations) : IAiChatbotUsage
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AiChatbotRef>>> ListByAgentAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await automations.Database.SqlQuery<UsageRow>(
            $"""
            SELECT DISTINCT node->>'agentId' AS "AgentId", c.id AS "ChatbotId", c.name AS "Name"
              FROM automations.chatbots c
              JOIN automations.chatbot_versions v ON v.id = c.published_version_id AND v.tenant_id = c.tenant_id
             CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(v.flow->'nodes') = 'array' THEN v.flow->'nodes' ELSE '[]'::jsonb END) AS node
             WHERE c.tenant_id = {tenantId}
               AND node->>'kind' = 'aiAgent'
               AND node->>'agentId' IS NOT NULL
            """).ToListAsync(cancellationToken);

        // El id se compara en C#: un agentId mal formado en un flujo viejo no tumba la lista.
        return rows
            .Select(row => (Agent: Guid.TryParse(row.AgentId, out var agentId) ? agentId : (Guid?)null, row))
            .Where(entry => entry.Agent is not null)
            .GroupBy(entry => entry.Agent!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AiChatbotRef>)
                [
                    .. group.Select(entry => new AiChatbotRef(entry.row.ChatbotId, entry.row.Name))
                        .DistinctBy(chatbot => chatbot.Id)
                        .OrderBy(chatbot => chatbot.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(chatbot => chatbot.Id),
                ]);
    }

    private sealed record UsageRow(string? AgentId, Guid ChatbotId, string Name);
}
