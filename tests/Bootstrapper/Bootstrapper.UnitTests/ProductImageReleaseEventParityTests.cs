using Modules.Catalog.Application;
using Modules.Storage.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-08: Catalog escribe el evento de fotos soltadas en el outbox con
/// <see cref="CatalogIntegrationEvents.ProductImagesReleased"/> y Storage se suscribe con
/// <see cref="StorageSubscribedEvents.ProductImagesReleased"/>: son dos copias, porque ningún módulo referencia
/// al otro. Si el nombre cambia en una sola, Storage nunca recibe el evento y las fotos se quedan en el bucket
/// sin error en ningún lado. Este proyecto ve los dos módulos. Los campos del payload los amarran las pruebas
/// de cada lado con el mismo literal.
/// </summary>
public sealed class ProductImageReleaseEventParityTests
{
    [Fact]
    public void TheReleasedEventNameIsTheOneStorageSubscribesTo() =>
        Assert.Equal(CatalogIntegrationEvents.ProductImagesReleased, StorageSubscribedEvents.ProductImagesReleased);

    // El nombre del contrato, literal: un cambio en las dos copias a la vez también tiene que ser deliberado.
    [Fact]
    public void BothCopiesAreTheNameInTheEventContract()
    {
        const string contract = "catalog.product.images-released.v1";

        Assert.Equal(contract, CatalogIntegrationEvents.ProductImagesReleased);
        Assert.Equal(contract, StorageSubscribedEvents.ProductImagesReleased);
    }
}
