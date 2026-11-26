namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-17: encola el aviso <see cref="MessagingIntegrationEvents.TemplateHeaderImageReleased"/> para que Storage despublique y borre
/// la imagen del encabezado de una plantilla. No guarda: el aviso viaja con el <c>SaveChangesAsync</c> de quien llama, en la misma transacción
/// que la eliminación de la fila, o no sale nada. Copia de <c>IProductImageReleasePublisher</c> (Catalog).
/// </summary>
public interface ITemplateHeaderImageReleases
{
    void Publish(Guid tenantId, Guid fileId, DateTimeOffset occurredAt);
}
