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
}
