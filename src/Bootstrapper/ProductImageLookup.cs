using Modules.Catalog.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Adapta el almacén de archivos de `Storage` al puerto que `catalog` declara.
///
/// **Vive acá y no en ninguno de los dos módulos, y eso es la decisión 1 del spec de `CAT-05`.**
/// `Modules.Catalog.Application` no referencia `Modules.Storage.Application`: el acoplamiento
/// entre dos módulos de negocio queda en el composition root, que ya referencia a los dos y cuyo
/// trabajo es exactamente cablearlos. `CatalogLayerTests` verifica que siga siendo así.
///
/// **No decide nada.** Traduce el agregado de `Storage` al vocabulario de `catalog` y devuelve el
/// dato crudo, incluido el `TenantId`: las reglas —tenant, disponibilidad y tipo— son de
/// `ProductImageResolver`. Filtrar por tenant acá sería más cómodo y escondería la garantía
/// justamente donde no se puede probar contra el mecanismo ausente.
/// </summary>
internal sealed class ProductImageLookup(
    IFileResourceRepository repository,
    IPublicObjectStorage publicStorage) : IProductImageLookup
{
    public async Task<ProductImageRef?> FindAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);
        return resource is null ? null : ToRef(resource);
    }

    public async Task<IReadOnlyDictionary<Guid, ProductImageRef>> FindManyAsync(
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, ProductImageRef>();

        // Una lectura por id. `IFileResourceRepository` no tiene un método por lote y agregárselo
        // es cambiarle el contrato a `Storage` desde afuera, que es lo que este slice evita.
        // Deuda declarada en el spec de CAT-05b: con muchos productos con portada conviene un
        // `GetManyAsync` propio de `Storage`, pedido por su dueño.
        foreach (var fileId in fileIds.Distinct())
        {
            var resource = await repository.GetAsync(
                new FileResourceId(fileId), cancellationToken);
            if (resource is not null)
            {
                found[fileId] = ToRef(resource);
            }
        }

        return found;
    }

    private ProductImageRef ToRef(FileResource resource) =>
        new(
            resource.Id.Value,
            resource.TenantId,
            resource.MimeType,
            resource.Status == FileResourceStatus.Available,
            PublicUrlOf(resource));

    // Misma condición que `StorageDtos`: hay URL pública sólo si el archivo fue publicado —tiene
    // PublicStorageKey— y el almacenamiento público está configurado. Sin las dos, `null`, y el
    // producto simplemente no trae URL.
    private string? PublicUrlOf(FileResource resource) =>
        resource.PublicStorageKey is { } key && publicStorage.IsConfigured
            ? publicStorage.GetUrl(key)
            : null;
}
