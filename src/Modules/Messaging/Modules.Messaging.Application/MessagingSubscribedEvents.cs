namespace Modules.Messaging.Application;

/// <summary>Los eventos de otros módulos que Messaging consume del outbox de plataforma. Messaging no referencia a sus publicadores:
/// el nombre es contrato, y una prueba de paridad en Bootstrapper.UnitTests amarra cada copia con la original.</summary>
public static class MessagingSubscribedEvents
{
    /// <summary>Spec 2026-11-10 (borrar clientes) A-9: adjuntos de un cliente borrado que quedaron sin dueño en R2. Payload camelCase
    /// <c>{ tenantId, customerId, storageKeys }</c>. Publicado por Customers.</summary>
    public const string CustomerMediaReleased = "customers.customer.media-released.v1";
}
