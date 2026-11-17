using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-03 D-6: la regla única de los handlers que sueltan la corrida viva del asistente (resolver, tomar, transferir,
/// devolver al asistente). Se encola la señal <c>ReleaseRun</c> si el asistente no era <c>None</c> antes del cambio y hubo algún
/// cambio. Vive en el handler y no en el agregado a propósito: el bot también resuelve con <c>Conversation.Resolve</c> y esa ruta no
/// debe soltarse a sí misma. Pausar, continuar, iniciar, liberar, reabrir y el eco no la usan (D-6a).</summary>
public static class ReleaseRunSignal
{
    public static void EnqueueIfAssisted(
        this IAutomationInbox inbox, Conversation conversation, ConversationAutomationStatus before, int changes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(conversation);
        if (before != ConversationAutomationStatus.None && changes > 0)
        {
            inbox.EnqueueReleaseRun(conversation.TenantId, conversation.ConnectionId, conversation.Id, now);
        }
    }
}
