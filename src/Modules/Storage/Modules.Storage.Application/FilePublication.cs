using BuildingBlocks.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

/// <summary>
/// Copia un recurso (y sus variantes) al bucket público, o retira esas copias. Extraído de
/// `PublishFileHandler`/`SoftDeleteFileHandler` (decisión 5 del spec 2026-09-19) para que el
/// adaptador de logo del tenant en Bootstrapper lo use sin pasar por esos handlers ni por sus
/// permisos — publicar el logo lo autoriza `tenancy.settings.update`, no `storage.file.publish`.
///
/// No decide nada de autorización ni de auditoría: eso se queda en cada handler que lo llama, que
/// es quien sabe quién y por qué. Tampoco decide el `now` de sus llamadores: tiene su propio
/// `IClock` para poder cerrar su firma sin un parámetro `occurredAt` — dos llamados del mismo
/// request pueden diferir en microsegundos frente al `now` que el handler usa para auditoría o
/// para `SoftDelete`, algo que ningún caso de uso observa hoy.
/// </summary>
public sealed class FilePublication(IPublicObjectStorage publicStorage, IClock clock)
{
    public async Task<string> PublishAsync(FileResource resource, CancellationToken cancellationToken)
    {
        if (!publicStorage.IsConfigured)
        {
            throw new StorageDomainException(
                "storage.public.not_configured",
                "Public image storage is not configured.");
        }

        resource.EnsureDownloadable();
        var publicKey = resource.PublicStorageKey ?? StorageKey.PublicFor(
            resource.TenantId, resource.Id, resource.Name);
        // Validar todos los invariantes de publicación (imagen, clave) antes de crear cualquier
        // objeto público.
        resource.Publish(publicKey, clock.UtcNow);
        var copiedKeys = new List<string>();

        try
        {
            await publicStorage.CopyFromPrivateAsync(resource.StorageKey, publicKey, cancellationToken);
            copiedKeys.Add(publicKey);
            foreach (var variant in resource.Variants)
            {
                var variantKey = StorageKey.PublicVariantFor(publicKey, variant);
                await publicStorage.CopyFromPrivateAsync(variant.StorageKey, variantKey, cancellationToken);
                copiedKeys.Add(variantKey);
            }
        }
        catch
        {
            foreach (var key in copiedKeys)
            {
                try { await publicStorage.DeleteAsync(key, CancellationToken.None); }
                catch { /* best-effort rollback; retrying publish is safe */ }
            }
            throw;
        }

        return publicKey;
    }

    public async Task UnpublishAsync(FileResource resource, CancellationToken cancellationToken)
    {
        if (resource.PublicStorageKey is not { } publicKey)
        {
            return;
        }

        await publicStorage.DeleteAsync(publicKey, cancellationToken);
        foreach (var variant in resource.Variants)
        {
            await publicStorage.DeleteAsync(
                StorageKey.PublicVariantFor(publicKey, variant), cancellationToken);
        }

        resource.Unpublish(clock.UtcNow);
    }
}
