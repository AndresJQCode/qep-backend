using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-31 D-7: cuándo el bot de verdad está atendiendo una conversación. Una sola regla para el procesador (que decide si una
/// corrida viva sigue o la interrumpió una persona) y para quien muestra o valida el estado del asistente, para que no diverjan.
/// </summary>
public static class BotAttendance
{
    /// <summary>El nombre de <c>AutomationPauseReason.Manual</c> de Mensajería.</summary>
    private const string ManualPause = "Manual";

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

    /// <summary>Spec 2026-11-01 D-4.2 a D-4.4: lo que la conversación y su corrida dicen sobre retomar el paso pendiente. Pausa
    /// <c>Manual</c>, corrida <c>Waiting</c> (una <c>Running</c> con el candado libre es una unidad muerta) y nadie la devolvió después de
    /// que arrancó: sin esto último, «Devolver» y luego «Pausar» resucitaría una corrida que P2-R25 ya dio por terminada. El resto
    /// (<c>automationStart</c>, el chatbot de la conexión y los eventos de un asesor) lo agrega <see cref="ManualRunStarter"/>, que tiene
    /// con qué leerlo.</summary>
    public static bool CanContinue(AutomationConversationState conversation, ConversationRun? run)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation is { AutomationStatus: AutomationStates.Paused, AutomationPauseReason: ManualPause, AutomationPausedAt: not null }
            && run is { Status: RunStatus.Waiting }
            && !(conversation.AutomationResumedAt > run.StartedAt);
    }
}
