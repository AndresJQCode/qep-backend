using Modules.Messaging.Application;
using Modules.Storage.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-17 («No orphaned images»): Messaging escribe el aviso de la imagen de encabezado liberada con
/// <see cref="MessagingIntegrationEvents.TemplateHeaderImageReleased"/> y Storage se suscribe con
/// <see cref="StorageSubscribedEvents.TemplateHeaderImageReleased"/>: dos copias, porque ningún módulo referencia al otro. Si el nombre
/// cambia en una sola, Storage nunca recibe el aviso y la imagen queda pública sin error en ningún lado. Copia de
/// <see cref="ProductImageReleaseEventParityTests"/>.
/// </summary>
public sealed class TemplateHeaderImageReleaseEventParityTests
{
    [Fact]
    public void TheProducerAndTheConsumerNameTheSameEvent()
    {
        // El nombre del contrato, literal: un cambio en las dos copias a la vez también tiene que ser deliberado.
        const string contract = "messaging.template.header-image-released.v1";

        Assert.Equal(MessagingIntegrationEvents.TemplateHeaderImageReleased, StorageSubscribedEvents.TemplateHeaderImageReleased);
        Assert.Equal(contract, MessagingIntegrationEvents.TemplateHeaderImageReleased);
        Assert.Equal(contract, StorageSubscribedEvents.TemplateHeaderImageReleased);
    }
}
