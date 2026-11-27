using Modules.Hr.Application;
using Modules.Storage.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado»): Hr escribe el aviso de los PDF que soltó con
/// <see cref="HrIntegrationEvents.DocumentsReleased"/> y Storage se suscribe con
/// <see cref="StorageSubscribedEvents.HrDocumentsReleased"/>: dos copias, porque ningún módulo referencia al otro. Si el nombre cambia en
/// una sola, Storage nunca recibe el aviso y el PDF queda en R2 para siempre, sin error en ningún lado. Copia de
/// <see cref="ChatbotMediaReleasedEventParityTests"/>.
/// </summary>
public sealed class HrDocumentsReleasedEventParityTests
{
    [Fact]
    public void TheProducerAndTheConsumerNameTheSameEvent()
    {
        // El nombre del contrato, literal: un cambio en las dos copias a la vez también tiene que ser deliberado.
        const string contract = "hr.employee.documents-released.v1";

        Assert.Equal(HrIntegrationEvents.DocumentsReleased, StorageSubscribedEvents.HrDocumentsReleased);
        Assert.Equal(contract, HrIntegrationEvents.DocumentsReleased);
        Assert.Equal(contract, StorageSubscribedEvents.HrDocumentsReleased);
    }
}
