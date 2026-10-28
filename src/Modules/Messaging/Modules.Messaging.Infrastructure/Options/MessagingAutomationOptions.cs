namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-21 D-M1: <c>Messaging:Automation:Enqueue</c> prende el INSERT en <c>messaging.automation_inbox</c> durante la
/// ingesta. Prendido por defecto desde que el motor de Automations (<c>AutomationInboxWorker</c>) consume la cola; apagarlo deja a los
/// chatbots publicados sin contestar.</summary>
public sealed class MessagingAutomationOptions
{
    public const string Section = "Messaging:Automation";

    public bool Enqueue { get; set; } = true;
}
