using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-10 (borrar clientes) A-9: Customers publica <see cref="CustomersIntegrationEvents.CustomerMediaReleased"/> y Messaging lo
/// consume con su propia copia del nombre, porque ningún módulo referencia al otro. Si un lado lo renombra, los objetos de R2 quedan
/// huérfanos sin error en ningún lado. Este proyecto ve los dos módulos.
/// </summary>
public sealed class CustomerMediaReleasedEventParityTests
{
    [Fact]
    public void TheEventNameMatchesInBothModules() =>
        Assert.Equal(CustomersIntegrationEvents.CustomerMediaReleased, MessagingSubscribedEvents.CustomerMediaReleased);

    // El literal del contrato: un cambio en las dos copias a la vez también tiene que ser deliberado.
    [Fact]
    public void TheEventNameIsTheOneInTheContract() =>
        Assert.Equal("customers.customer.media-released.v1", MessagingSubscribedEvents.CustomerMediaReleased);
}
