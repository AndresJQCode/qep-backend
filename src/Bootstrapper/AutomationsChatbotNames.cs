using Microsoft.EntityFrameworkCore;
using Modules.Ai.Infrastructure.Persistence;
using Modules.Automations.Infrastructure.Persistence;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-I3: el nombre de los chatbots que firman mensajes, leído de Automations por tenant e ids. Spec 2026-10-29 §4.4:
/// también el de los agentes de IA, leído de Ai, en el mismo diccionario (los ids no se repiten entre los dos). Un id que no existe (bot
/// borrado) no aparece: el mapeo de Mensajería lo muestra como «Chatbot» o «Agente IA».</summary>
internal sealed class AutomationsChatbotNames(AutomationsDbContext context, AiDbContext ai) : IAutomationsChatbotNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chatbotIds);
        if (chatbotIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = chatbotIds.Distinct().ToArray();
        var names = await context.Chatbots.AsNoTracking()
            .Where(chatbot => chatbot.TenantId == tenantId && ids.Contains(chatbot.Id))
            .Select(chatbot => new { chatbot.Id, chatbot.Name })
            .ToDictionaryAsync(chatbot => chatbot.Id, chatbot => chatbot.Name, ct);
        if (names.Count == ids.Length)
        {
            return names;
        }

        var missing = ids.Where(id => !names.ContainsKey(id)).ToArray();
        var agents = await ai.Agents.AsNoTracking()
            .Where(agent => agent.TenantId == tenantId && missing.Contains(agent.Id))
            .Select(agent => new { agent.Id, agent.Name })
            .ToListAsync(ct);
        foreach (var agent in agents)
        {
            names[agent.Id] = agent.Name;
        }

        return names;
    }
}
