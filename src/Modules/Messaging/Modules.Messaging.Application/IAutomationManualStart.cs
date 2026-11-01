using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-31: puerto hacia Automations para iniciar el asistente desde el hilo. Mensajería no ve el módulo; el
/// adaptador vive en Bootstrapper. La decisión (asesor, bloqueo, canal, ventana, chatbot) es de Automations: la misma que
/// aplica el motor, para que el botón del encabezado y el rechazo del POST no puedan discrepar.</summary>
public interface IAutomationManualStart
{
    /// <summary>Spec 2026-11-01 D-6: una sola lectura para los dos campos del resumen. <see cref="AutomationAssistantState.Start"/>
    /// <c>null</c>: el encabezado no ofrece el botón; si no, uno de <see cref="AutomationStartStates"/>.
    /// <see cref="AutomationAssistantState.Continue"/> <c>null</c>: no hay modal de dos opciones. Los dos son <c>null</c> si la evaluación
    /// falla (se registra y no sube): el resumen corre después del commit de tomar, devolver, pausar, resolver o reabrir, y un 500 ahí
    /// contestaría error por un cambio ya hecho. <c>null</c> es la dirección segura: sin botón, sigue «Devolver al asistente». La
    /// cancelación sí sube.</summary>
    Task<AutomationAssistantState> FindStateAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Repite la decisión bajo el candado de la conversación y, sólo si se puede iniciar, llama a <paramref name="commit"/>
    /// con el chatbot que atiende la conexión. Si no se puede, lanza el error con su código.</summary>
    Task StartAsync(Guid tenantId, Guid conversationId, Func<Guid, CancellationToken, Task> commit, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-01 D-8: lo mismo para retomar el paso pendiente de una pausa manual. Si ya no hay paso, lanza
    /// <see cref="MessagingErrorCodes.AutomationCannotContinue"/> y no cae a devolver al asistente.</summary>
    Task ContinueAsync(Guid tenantId, Guid conversationId, Func<CancellationToken, Task> commit, CancellationToken cancellationToken);
}

/// <summary>Lo que el resumen de una conversación lleva del asistente: <see cref="ConversationSummary.AutomationStart"/> y
/// <see cref="ConversationSummary.AutomationContinue"/>.</summary>
public sealed record AutomationAssistantState(string? Start, AutomationContinueDto? Continue)
{
    /// <summary>La evaluación falló: sin botón ni modal.</summary>
    public static AutomationAssistantState Unknown { get; } = new(null, null);
}

/// <summary>Los valores de <see cref="ConversationSummary.AutomationStart"/>, por nombre (BFF: la pantalla no conoce el enum del motor).</summary>
public static class AutomationStartStates
{
    public const string Available = "Available";
    public const string WindowClosed = "WindowClosed";
    public const string NoChatbot = "NoChatbot";
}

/// <summary>Los valores de <see cref="AutomationContinueDto.Mode"/>, por nombre (BFF: la pantalla no conoce la corrida).</summary>
public static class AutomationContinueModes
{
    public const string PendingStep = "PendingStep";
    public const string FlowStart = "FlowStart";
}
