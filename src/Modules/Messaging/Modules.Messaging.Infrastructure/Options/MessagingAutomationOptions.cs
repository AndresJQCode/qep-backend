namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-21 D-M1: <c>Messaging:Automation:Enqueue</c> prende el INSERT en <c>messaging.automation_inbox</c> durante la
/// ingesta. Apagado por defecto: mientras nadie consuma la cola (el worker del motor llega después) no tiene sentido llenarla.</summary>
public sealed class MessagingAutomationOptions
{
    public const string Section = "Messaging:Automation";

    public bool Enqueue { get; set; }
}
