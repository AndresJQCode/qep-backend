namespace Modules.Catalog.Application;

/// <summary>
/// Nombres de los eventos de integración que Catalog escribe en el outbox para otros módulos.
/// </summary>
public static class CatalogIntegrationEvents
{
    /// <summary>
    /// Fotos de producto que dejaron de usarse (las quitó un PUT o las arrastró un borrado).
    /// Payload: <c>{ "tenantId": "&lt;guid&gt;", "fileIds": ["&lt;guid&gt;", …] }</c>. Lo consume Storage.
    /// </summary>
    public const string ProductImagesReleased = "catalog.product.images-released.v1";
}
