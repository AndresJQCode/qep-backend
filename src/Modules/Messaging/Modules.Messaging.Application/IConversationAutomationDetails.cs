using System.Text.Json;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 §5.5: lo que el asesor ve como «Datos capturados» de la corrida más reciente de la conversación. Declarado
/// en Messaging y adaptado en <c>Bootstrapper/ConversationAutomationDetails.cs</c> sobre Automations (D-C10: el asesor no tiene
/// <c>automations.chatbot.read</c>). <see cref="Status"/> es el nombre del <c>RunStatus</c>; <see cref="StepTitle"/> sólo existe mientras
/// la corrida está viva. <see cref="FailureCode"/> (spec 2026-10-24 D-T8) es el <c>failure_code</c> de la corrida cuando falló
/// (<c>automations.run.loop_detected</c>, <c>messaging.connection_unavailable</c>, …); la causa completa vive en el evento del hilo.</summary>
public sealed record ConversationAutomationDetails(
    Guid ChatbotId,
    string ChatbotName,
    int VersionNumber,
    string Status,
    string? StepTitle,
    JsonElement Variables,
    IReadOnlyList<ConversationAutomationTraceEntry> Trace,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? FailureCode = null);

public sealed record ConversationAutomationTraceEntry(DateTimeOffset At, string NodeId, string Kind, string Outcome);

public interface IConversationAutomationDetails
{
    /// <summary>La corrida más reciente de la conversación, o <c>null</c> si nunca tuvo una. Todo por <paramref name="tenantId"/>.</summary>
    Task<ConversationAutomationDetails?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}
