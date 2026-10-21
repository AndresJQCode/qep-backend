namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 D-I3: los nombres de los chatbots que firman mensajes. Puerto de Messaging que implementa el
/// módulo Automations (adaptador real en la tarea 16); mientras tanto el Bootstrapper registra uno nulo. Lo que no
/// aparece en el diccionario sale como «Chatbot» en el mapeo.</summary>
public interface IAutomationsChatbotNames
{
    Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken ct);
}
