using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// Storage → Store: el banner. A diferencia de <see cref="ProductImageLookup"/>, que devuelve el dato crudo y deja la regla a su
/// resolver, acá la regla vive en el adaptador (como en <see cref="TenantLogoStorage"/>): «qué archivo puede ser el banner de una
/// tienda» es una pregunta de Storage que Store sólo necesita responder con una URL o con nada.
/// </summary>
internal sealed class StoreFileLookup(IFileResourceRepository repository, IPublicObjectStorage publicStorage) : IStoreFileLookup
{
    // Los mismos tipos que admite el logo del tenant: sin SVG, que serviría script desde el bucket público.
    private static readonly HashSet<string> ImageMimeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp" };

    public async Task<string?> FindPublicImageAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        var resource = await repository.GetAsync(new FileResourceId(fileId), cancellationToken);

        // Un solo null para «no existe», «es de otro tenant» y «no sirve»: distinguirlos confirmaría que el id existe en otro tenant.
        if (resource is null
            || resource.TenantId != tenantId
            || resource.OwnerType is not FileOwnerType.Store
            || resource.OwnerId != tenantId
            || resource.Status is not FileResourceStatus.Available
            || !ImageMimeTypes.Contains(resource.MimeType)
            || resource.PublicStorageKey is not { } key
            || !publicStorage.IsConfigured)
        {
            return null;
        }

        return publicStorage.GetUrl(key);
    }
}
