using Modules.Automations.Domain;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-31 D-7: cuándo el bot de verdad está atendiendo una conversación. Una sola regla para el procesador (que decide si una
/// corrida viva sigue o la interrumpió una persona) y para quien muestra o valida el estado del asistente, para que no diverjan.
/// </summary>
public static class BotAttendance
{
    /// <summary>D-M15. Tomar y devolver al asistente no deja rastro en el estado (tomar pausa y no encola, devolver vuelve a
    /// <c>Active</c>): por eso P2-R25 compara la hora en que una persona la devolvió con el arranque de la corrida.</summary>
    public static bool HumanTookOver(AutomationConversationState conversation, ConversationRun run)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(run);
        return conversation.AssignedMemberId is not null
            || conversation.Status == ConversationStates.Resolved
            || conversation.AutomationStatus is AutomationStates.Paused or AutomationStates.None
            || conversation.AutomationResumedAt > run.StartedAt;
    }

    /// <summary>Hay una corrida viva y ninguna persona la interrumpió (<see cref="HumanTookOver"/>).</summary>
    public static bool IsAttending(AutomationConversationState conversation, ConversationRun? live) =>
        live is { IsLive: true } && !HumanTookOver(conversation, live);
}
