using System.Text.Json;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 §5.5: lo que el asesor ve como «Datos capturados» de la corrida más reciente de la conversación. Declarado
/// en Messaging y adaptado en <c>Bootstrapper/ConversationAutomationDetails.cs</c> sobre Automations (D-C10: el asesor no tiene
/// <c>automations.chatbot.read</c>). <see cref="Status"/> es el nombre del <c>RunStatus</c>; <see cref="StepTitle"/> sólo existe mientras
/// la corrida está viva.</summary>
public sealed record ConversationAutomationDetails(
    Guid ChatbotId,
    string ChatbotName,
    int VersionNumber,
    string Status,
    string? StepTitle,
    JsonElement Variables,
    IReadOnlyList<ConversationAutomationTraceEntry> Trace,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record ConversationAutomationTraceEntry(DateTimeOffset At, string NodeId, string Kind, string Outcome);

public interface IConversationAutomationDetails
{
    /// <summary>La corrida más reciente de la conversación, o <c>null</c> si nunca tuvo una. Todo por <paramref name="tenantId"/>.</summary>
    Task<ConversationAutomationDetails?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}
