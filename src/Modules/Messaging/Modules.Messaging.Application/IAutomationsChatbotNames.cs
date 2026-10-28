namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 D-I3: los nombres de los chatbots que firman mensajes. Puerto de Messaging que implementa el
/// Bootstrapper con un adaptador que lee <c>AutomationsDbContext</c> (<c>Bootstrapper/AutomationsChatbotNames.cs</c>). Lo que no
/// aparece en el diccionario (un chatbot borrado) sale como «Chatbot» en el mapeo.</summary>
public interface IAutomationsChatbotNames
{
    Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken ct);
}
