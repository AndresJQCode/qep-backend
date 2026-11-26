using Modules.Automations.Application;
using Modules.Storage.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-18 («Lifecycle»): Automations escribe el aviso de los archivos de un chatbot borrado con
/// <see cref="AutomationsIntegrationEvents.ChatbotMediaReleased"/> y Storage se suscribe con
/// <see cref="StorageSubscribedEvents.ChatbotMediaReleased"/>: dos copias, porque ningún módulo referencia al otro. Si el nombre cambia en
/// una sola, Storage nunca recibe el aviso y los archivos quedan públicos hasta el barrido diario, sin error en ningún lado. Copia de
/// <see cref="TemplateHeaderImageReleaseEventParityTests"/>.
/// </summary>
public sealed class ChatbotMediaReleasedEventParityTests
{
    [Fact]
    public void TheProducerAndTheConsumerNameTheSameEvent()
    {
        // El nombre del contrato, literal: un cambio en las dos copias a la vez también tiene que ser deliberado.
        const string contract = "automations.chatbot.media-released.v1";

        Assert.Equal(AutomationsIntegrationEvents.ChatbotMediaReleased, StorageSubscribedEvents.ChatbotMediaReleased);
        Assert.Equal(contract, AutomationsIntegrationEvents.ChatbotMediaReleased);
        Assert.Equal(contract, StorageSubscribedEvents.ChatbotMediaReleased);
    }
}
