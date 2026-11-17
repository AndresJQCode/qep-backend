using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 §10 P1: la imagen destacada de Shopify entra a la galería en una segunda pasada, fuera de la transacción del
/// producto. Se guarda como archivo <see cref="FileOwnerType.Product"/> de Storage (dueño = el producto, visible en la consola, por
/// la subida de servidor) y Catalog la pone en lugar de la que puso la importación (<see cref="IExternalProductWriter.ReplaceImageAsync"/>,
/// que libera el archivo que sobra). Vive en el composition root porque Shopify no referencia Catalog ni Storage.
/// <para>Un scope propio por paso: un <c>concurrency.conflict</c> de Catalog (el asesor guardó el producto mientras tanto) se reintenta
/// una vez con el producto releído. Un rechazo de Storage (declaración o contenido) o de Catalog vuelve como
/// <see cref="ShopifyProductCodes.ImageFailed"/>; nunca lanza por la imagen, sólo por la base o la red.</para>
/// </summary>
internal sealed class ShopifyProductImageStore(IServiceScopeFactory scopes, IClock clock) : IShopifyProductImageStore
{
    private const string ConcurrencyConflict = "concurrency.conflict";

    public async Task<ShopifyImageAttach> AttachAsync(
        Guid tenantId, Guid productId, ShopifyFetchedImage image, Guid? previousFileId, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        SystemStoredFile stored;
        await using (var scope = scopes.CreateAsyncScope())
        {
            try
            {
                stored = await scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>().StoreAsync(
                    tenantId,
                    FileOwnerType.Product,
                    productId,
                    FileNameFor(productId, image.ContentType),
                    image.ContentType,
                    image.Content,
                    IShopifyImageFetcher.MaxBytes,
                    cancellationToken);
            }
            catch (StorageDomainException)
            {
                return new ShopifyImageAttach(null, ShopifyProductCodes.ImageFailed);
            }
        }

        if (stored.Outcome != SystemUploadOutcome.Available)
        {
            return new ShopifyImageAttach(null, ShopifyProductCodes.ImageFailed);
        }

        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                var entered = await scope.ServiceProvider.GetRequiredService<IExternalProductWriter>().ReplaceImageAsync(
                    tenantId, productId, previousFileId, stored.FileId, actorId, clock.UtcNow, cancellationToken);
                // Si no entró (el asesor quitó la imagen importada y puso otras), Catalog ya liberó el archivo nuevo: no es una falla.
                return new ShopifyImageAttach(entered ? stored.FileId : null, null);
            }
            catch (RequestConcurrencyException exception) when (exception.Code == ConcurrencyConflict && attempt == 1)
            {
                // Otro guardado del producto ganó: un scope nuevo relee y vuelve a intentar una vez.
            }
            catch (Exception exception) when (exception is CatalogDomainException or RequestConcurrencyException)
            {
                // El archivo ya está Available y no entró a ninguna galería: sin liberarlo quedaría sin dueño para siempre.
                await ReleaseAsync(tenantId, stored.FileId, cancellationToken);
                return new ShopifyImageAttach(null, ShopifyProductCodes.ImageFailed);
            }
        }
    }

    private async Task ReleaseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProductImageReleasePublisher>().Publish(tenantId, [fileId], clock.UtcNow);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>().SaveChangesAsync(cancellationToken);
    }

    private static string FileNameFor(Guid productId, string contentType) => contentType switch
    {
        "image/png" => $"shopify-{productId:N}.png",
        "image/webp" => $"shopify-{productId:N}.webp",
        _ => $"shopify-{productId:N}.jpg",
    };
}
