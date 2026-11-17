using Modules.Ai.Application;
using Modules.Automations.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 (agente IA en chatbots) §5.2: los agentes del tenant para el validador y el editor de chatbots. Automations no ve Ai (D-A3),
/// así que el cruce va acá, como <see cref="AutomationsChatbotNames"/>. El estado viaja por nombre (<c>AgentStatus</c>): el validador de
/// Automations lo compara con <c>FlowAgentRef</c>. Proveedor y modelo son los de la versión publicada, que es la que corre (D-B5); un
/// borrador muestra los de su configuración.
/// </summary>
internal sealed class AutomationsAgentDirectory(IAgentRepository agents) : IAutomationsAgentDirectory
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
}
