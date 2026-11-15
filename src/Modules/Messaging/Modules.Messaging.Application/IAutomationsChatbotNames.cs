namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 D-I3: los nombres de los bots que firman mensajes o atienden conversaciones. Puerto de Messaging que implementa
/// el Bootstrapper con un adaptador que lee <c>AutomationsDbContext</c> y, desde el spec 2026-10-29 §4.4, también <c>AiDbContext</c>
/// (<c>Bootstrapper/AutomationsChatbotNames.cs</c>): un mismo diccionario para chatbots y agentes, porque los ids no se repiten entre los
/// dos. Lo que no aparece (un bot borrado) sale como «Chatbot» o «Agente IA» en el mapeo.</summary>
public interface IAutomationsChatbotNames
{
    Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(Guid tenantId, IReadOnlyCollection<Guid> chatbotIds, CancellationToken ct);
}
