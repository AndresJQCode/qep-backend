namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-31: puerto hacia Automations para iniciar el asistente desde el hilo. Mensajería no ve el módulo; el
/// adaptador vive en Bootstrapper. La decisión (asesor, bloqueo, canal, ventana, chatbot) es de Automations: la misma que
/// aplica el motor, para que el botón del encabezado y el rechazo del POST no puedan discrepar.</summary>
public interface IAutomationManualStart
{
    /// <summary><c>null</c>: el encabezado no ofrece el botón. Si no, uno de <see cref="AutomationStartStates"/>.</summary>
    Task<string?> FindStateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Repite la decisión bajo el candado de la conversación y, sólo si se puede iniciar, llama a <paramref name="commit"/>
    /// con el chatbot que atiende la conexión. Si no se puede, lanza el error con su código.</summary>
    Task StartAsync(Guid tenantId, Guid conversationId, Func<Guid, CancellationToken, Task> commit, CancellationToken cancellationToken);
}

/// <summary>Los valores de <see cref="ConversationSummary.AutomationStart"/>, por nombre (BFF: la pantalla no conoce el enum del motor).</summary>
public static class AutomationStartStates
{
    public const string Available = "Available";
    public const string WindowClosed = "WindowClosed";
    public const string NoChatbot = "NoChatbot";
}
