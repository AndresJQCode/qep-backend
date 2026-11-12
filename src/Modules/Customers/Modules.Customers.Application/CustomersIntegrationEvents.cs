namespace Modules.Customers.Application;

/// <summary>Los eventos que Customers deja en el outbox de plataforma para otros módulos.</summary>
public static class CustomersIntegrationEvents
{
    /// <summary>
    /// Spec 2026-11-10 (borrar clientes) A-9: adjuntos de un cliente borrado que quedaron sin dueño en R2.
    /// Payload: <c>{ "tenantId": "&lt;guid&gt;", "customerId": "&lt;guid&gt;", "storageKeys": ["…"] }</c>. Lo consume
    /// Messaging (<c>messaging.customer-media-purge</c>), que borra los objetos con reintento después del commit.
    /// </summary>
    public const string CustomerMediaReleased = "customers.customer.media-released.v1";
}
