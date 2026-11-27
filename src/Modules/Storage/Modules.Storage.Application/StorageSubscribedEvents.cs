namespace Modules.Storage.Application;

/// <summary>
/// Nombres de los eventos de integración de otros módulos a los que Storage se suscribe. Es una copia del
/// nombre que el productor escribe en el outbox, porque ningún módulo referencia al otro: la paridad la
/// amarra <c>ProductImageReleaseEventParityTests</c> en Bootstrapper.UnitTests, que ve los dos.
/// </summary>
public static class StorageSubscribedEvents
{
    /// <summary>
    /// Fotos de producto que Catalog soltó (las quitó un PUT o las arrastró un borrado).
    /// Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileIds": ["&lt;guid&gt;", …] }</c>.
    /// </summary>
    public const string ProductImagesReleased = "catalog.product.images-released.v1";

    /// <summary>
    /// Spec 2026-11-17: la imagen fija del encabezado de una plantilla de WhatsApp que Messaging soltó (se borró la plantilla, el refresco
    /// la eliminó, o una creación falló sin poder liberarla). Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileId": "&lt;guid&gt;" }</c>.
    /// La paridad con Messaging la amarra <c>TemplateHeaderImageReleaseEventParityTests</c>.
    /// </summary>
    public const string TemplateHeaderImageReleased = "messaging.template.header-image-released.v1";

    /// <summary>
    /// Spec 2026-11-18 («Lifecycle»): los archivos de los bloques «Mensaje» de un chatbot que Automations borró (el borrador y cada versión
    /// publicada). Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileIds": ["&lt;guid&gt;", …] }</c>. Un archivo que otro chatbot, una plantilla
    /// guardada o un mensaje de la bandeja todavía usa no se suelta. La paridad con Automations la amarra
    /// <c>ChatbotMediaReleasedEventParityTests</c>.
    /// </summary>
    public const string ChatbotMediaReleased = "automations.chatbot.media-released.v1";

    /// <summary>
    /// Spec 2026-11-26 («PDF del registro eliminado»): los PDF de un empleado que Hr soltó (hoy, al eliminar un registro de cargo y
    /// lugar). Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileIds": ["&lt;guid&gt;", …] }</c>. Lo consume
    /// <c>storage.hr-document-release</c>, que purga sólo los que ninguna tabla de Hr del tenant nombra ya
    /// (<see cref="IHrDocumentReferences"/>). La paridad con Hr la amarra <c>HrDocumentsReleasedEventParityTests</c>.
    /// </summary>
    public const string HrDocumentsReleased = "hr.employee.documents-released.v1";
}
