using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-I3: adaptador provisional de <see cref="IAutomationsChatbotNames"/>. Contesta «Chatbot» para
/// todo id hasta que la tarea 16 conecte el módulo Automations; así los mensajes de un chatbot ya salen con autor.</summary>
internal sealed class NullAutomationsChatbotNames : IAutomationsChatbotNames
{
    public Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chatbotIds);
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(chatbotIds.ToDictionary(id => id, _ => "Chatbot"));
    }
}
