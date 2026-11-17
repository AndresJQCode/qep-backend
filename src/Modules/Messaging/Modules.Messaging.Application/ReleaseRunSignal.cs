using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-03 D-6: la regla única de los handlers que sueltan la corrida viva del asistente (resolver, tomar, transferir,
/// devolver al asistente). Se encola la señal <c>ReleaseRun</c> si el asistente no era <c>None</c> antes del cambio y hubo algún
/// cambio. Vive en el handler y no en el agregado a propósito: el bot también resuelve con <c>Conversation.Resolve</c> y esa ruta no
/// debe soltarse a sí misma. Continuar, iniciar, liberar, reabrir y el eco no la usan (D-6a); pausar a mano, sólo con una delegación viva.
/// <para>Revisión final de agentes de IA, hallazgo 5: la señal va al procesador del bot que tenía la conversación <b>antes</b> del cambio
/// (<paramref name="beforeBot"/>; resolver lo borra). Con el <c>bot_kind</c> por defecto, la de un agente la reclamaba el motor de chatbots
/// y la sesión del agente seguía viva.</para>
/// <para>Spec 2026-11-17 (agente IA en chatbots) §6.4: con una delegación viva antes del cambio (<paramref name="beforeDelegation"/>, leída
/// antes porque el agregado la limpia), la señal va a <b>los dos</b>: al agente, que cierra su sesión, y al chatbot, que cierra la corrida
/// que espera en el bloque. La delegación pasa a <c>Interrupted</c> en el mismo guardado; si ya había vuelto, se queda como estaba.</para></summary>
public static class ReleaseRunSignal
{
    public static async Task EnqueueIfAssistedAsync(
        this IAutomationInbox inbox, IBotDelegationRepository delegations, Conversation conversation, ConversationAutomationStatus before,
        BotKind? beforeBot, Guid? beforeDelegation, int changes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(delegations);
        ArgumentNullException.ThrowIfNull(conversation);
        if (before == ConversationAutomationStatus.None || changes <= 0)
        {
            return;
        }

        if (beforeDelegation is { } delegationId)
        {
            inbox.EnqueueReleaseRun(conversation.TenantId, conversation.ConnectionId, conversation.Id, BotKind.Agent, now);
            inbox.EnqueueReleaseRun(conversation.TenantId, conversation.ConnectionId, conversation.Id, BotKind.Chatbot, now);
            (await delegations.FindAsync(conversation.TenantId, delegationId, cancellationToken))?.Interrupt(now);
            return;
        }

        // Una conversación con asistente de antes de automation_bot_kind es de chatbot.
        inbox.EnqueueReleaseRun(conversation.TenantId, conversation.ConnectionId, conversation.Id, beforeBot ?? BotKind.Chatbot, now);
    }
}
