using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-03 D-6: la regla única de los handlers que sueltan la corrida viva del asistente (resolver, tomar, transferir,
/// devolver al asistente). Se encola la señal <c>ReleaseRun</c> si el asistente no era <c>None</c> antes del cambio y hubo algún
/// cambio. Vive en el handler y no en el agregado a propósito: el bot también resuelve con <c>Conversation.Resolve</c> y esa ruta no
/// debe soltarse a sí misma. Pausar, continuar, iniciar, liberar, reabrir y el eco no la usan (D-6a).
/// <para>Revisión final de agentes de IA, hallazgo 5: la señal va al procesador del bot que tenía la conversación <b>antes</b> del cambio
/// (<paramref name="beforeBot"/>; resolver lo borra). Con el <c>bot_kind</c> por defecto, la de un agente la reclamaba el motor de chatbots
/// y la sesión del agente seguía viva.</para></summary>
public static class ReleaseRunSignal
{
    public static void EnqueueIfAssisted(
        this IAutomationInbox inbox, Conversation conversation, ConversationAutomationStatus before, BotKind? beforeBot, int changes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(conversation);
        if (before != ConversationAutomationStatus.None && changes > 0)
        {
            // Una conversación con asistente de antes de automation_bot_kind es de chatbot.
            inbox.EnqueueReleaseRun(conversation.TenantId, conversation.ConnectionId, conversation.Id, beforeBot ?? BotKind.Chatbot, now);
        }
    }
}
