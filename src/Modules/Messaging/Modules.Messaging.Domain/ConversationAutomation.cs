namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-10-21 §4.2: si un asistente (bot) está atendiendo la conversación. <c>None</c> es lo normal: la atiende una
/// persona o nadie. <c>Paused</c> es un asistente que existió y un humano detuvo; sólo <c>ResumeAutomation</c> lo reactiva.</summary>
public enum ConversationAutomationStatus
{
    None,
    Active,
    Paused,
}

/// <summary>Spec 2026-10-21 §4.2: por qué se pausó el asistente. Viaja en el evento <c>AutomationPaused</c> y se guarda por nombre.</summary>
public enum AutomationPauseReason
{
    AdvisorTook,
    AdvisorReplied,
    AdvisorTransferred,
    Manual,
    Handoff,
}
