using Microsoft.EntityFrameworkCore;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Persistence;
using Modules.Automations.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 (agente IA en chatbots) §5.2 y §7: los agentes del tenant para el validador y el editor de chatbots, y el consumo de IA
/// por chatbot para la lista. Automations no ve Ai (D-A3), así que el cruce va acá, como <see cref="AutomationsChatbotNames"/>. El estado
/// viaja por nombre (<c>AgentStatus</c>): el validador de Automations lo compara con <c>FlowAgentRef</c>. Proveedor y modelo son los de la
/// versión publicada, que es la que corre (D-B5); un borrador muestra los de su configuración.
/// <para>
/// <b>Acoplamiento por nombre</b> (D-B15): el consumo lee <c>ai.usage_records</c> (<c>tenant_id</c>, <c>session_id</c>, <c>input_tokens</c>,
/// <c>output_tokens</c>, <c>called_at</c>) y <c>ai.sessions</c> (<c>id</c>, <c>tenant_id</c>, <c>chatbot_id</c>) con SQL. Renombrar una de esas
/// columnas compila y deja la lista de chatbots en 500.
/// </para>
/// </summary>
internal sealed class AutomationsAgentDirectory(IAgentRepository agents, AiDbContext ai) : IAutomationsAgentDirectory
{
    public async Task<IReadOnlyList<AutomationAgentRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [
            .. (await agents.ListAsync(tenantId, null, cancellationToken)).Select(agent =>
            {
                var published = agent.PublishedVersion;
                var draft = published is null ? agent.DraftConfig.Model : null;
                return new AutomationAgentRef(
                    agent.Id, agent.Name, agent.Status.ToString(), published?.Provider ?? draft?.Provider ?? string.Empty,
                    published?.Model ?? draft?.Id ?? string.Empty);
            }),
        ];

    public async Task<IReadOnlyDictionary<Guid, (long InputTokens, long OutputTokens)>> UsageByChatbotAsync(
        Guid tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var start = from.ToUniversalTime();
        var end = until.ToUniversalTime();
        var rows = await ai.Database.SqlQuery<UsageRow>(
            $"""
            SELECT s.chatbot_id AS "ChatbotId", sum(u.input_tokens)::bigint AS "InputTokens", sum(u.output_tokens)::bigint AS "OutputTokens"
              FROM ai.usage_records u
              JOIN ai.sessions s ON s.id = u.session_id AND s.tenant_id = u.tenant_id
             WHERE u.tenant_id = {tenantId}
               AND s.chatbot_id IS NOT NULL
               AND u.called_at >= {start} AND u.called_at < {end}
             GROUP BY s.chatbot_id
            """).ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.ChatbotId, row => (row.InputTokens, row.OutputTokens));
    }

    private sealed record UsageRow(Guid ChatbotId, long InputTokens, long OutputTokens);
}
