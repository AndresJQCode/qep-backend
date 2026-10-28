using Microsoft.EntityFrameworkCore;
using Modules.Automations.Infrastructure.Persistence;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-I3: el nombre de los chatbots que firman mensajes, leído de Automations por tenant e ids. Un id que no
/// existe (chatbot borrado) no aparece: el mapeo de Mensajería lo muestra como «Chatbot».</summary>
internal sealed class AutomationsChatbotNames(AutomationsDbContext context) : IAutomationsChatbotNames
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
        return await context.Chatbots.AsNoTracking()
            .Where(chatbot => chatbot.TenantId == tenantId && ids.Contains(chatbot.Id))
            .Select(chatbot => new { chatbot.Id, chatbot.Name })
            .ToDictionaryAsync(chatbot => chatbot.Id, chatbot => chatbot.Name, ct);
    }
}
