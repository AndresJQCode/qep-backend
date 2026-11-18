namespace Modules.Messaging.Application;

/// <summary>
/// Nombres de los eventos de integración que Messaging escribe en <c>platform.outbox_messages</c> para otros módulos.
/// </summary>
public static class MessagingIntegrationEvents
{
    /// <summary>
    /// Spec 2026-11-17 «No orphaned images»: la imagen fija del encabezado de una plantilla quedó sin fila (se borró la plantilla, el refresco
    /// la eliminó, o una creación falló y Storage no pudo liberarla en el momento). Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileId":
    /// "&lt;guid&gt;" }</c>. Lo consume Storage, que despublica y borra el archivo; Storage lo nombra igual en <c>StorageSubscribedEvents</c>.
    /// </summary>
    public const string TemplateHeaderImageReleased = "messaging.template.header-image-released.v1";
}
