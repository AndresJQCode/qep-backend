using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Purchases.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper;

/// <summary>
/// Catalog → Purchases (spec 2026-11-21): la foto del producto que Compras creó al confirmar la orden sale de la variante del
/// mayorista. Corre después del commit del evento (<see cref="WholesaleOrderEventApplier.CopyPendingImagesAsync"/>), con un scope
/// por paso, como <see cref="ShopifyProductImageStore"/>:
/// <list type="number">
/// <item>Lee la variante en el catálogo del **mayorista** y toma su foto (la de la variante o la portada). El archivo tiene que ser
/// del mayorista, estar <c>Available</c> y ser de dueño <see cref="FileOwnerType.Product"/>: una referencia a un archivo ajeno no se
/// copia.</item>
/// <item>Baja los bytes del almacén y los sube al tenant del **comprador** (dueño: el producto nuevo). La copia es propia: si el
/// mayorista borra o cambia su foto, el producto del distribuidor no cambia.</item>
/// <item>La pone en la galería con <see cref="IExternalProductWriter.ReplaceImageAsync"/>, sin imagen previa. Si Catalog no la
/// admite, la libera él; si falla, se libera acá: un archivo <c>Available</c> sin dueño no lo barre nadie.</item>
/// </list>
/// Que el mayorista no tenga foto, o que Storage rechace el contenido, no es un error: el producto queda sin imagen y el
/// distribuidor la sube a mano. Sólo la base o la red lanzan, y quien llama lo registra.
/// </summary>
internal sealed partial class PurchasesProductImages(
    IServiceScopeFactory scopes,
    ILogger<PurchasesProductImages> logger) : IPurchasesProductImages
{
    private const string ConcurrencyConflict = "concurrency.conflict";
    private const long MaxBytes = 10L * 1024 * 1024;

    public async Task CopyAsync(PendingProductImage image, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        var source = await ReadSourceAsync(image, cancellationToken);
        if (source is null)
        {
            return;
        }

        SystemStoredFile stored;
        await using (var scope = scopes.CreateAsyncScope())
        {
            try
            {
                stored = await scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>().StoreAsync(
                    image.BuyerTenantId,
                    FileOwnerType.Product,
                    image.ProductId,
                    FileNameFor(image.ProductId, source.MimeType),
                    source.MimeType,
                    source.Content,
                    MaxBytes,
                    cancellationToken);
            }
            catch (StorageDomainException)
            {
                LogRejected(logger, image.BuyerTenantId, image.ProductId);
                return;
            }
        }

        if (stored.Outcome != SystemUploadOutcome.Available)
        {
            LogRejected(logger, image.BuyerTenantId, image.ProductId);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                // Si no entró (el distribuidor ya puso otra foto), Catalog libera el archivo nuevo: no es una falla.
                await scope.ServiceProvider.GetRequiredService<IExternalProductWriter>().ReplaceImageAsync(
                    image.BuyerTenantId, image.ProductId, previousFileId: null, stored.FileId, Guid.Empty, now, cancellationToken);
                return;
            }
            catch (RequestConcurrencyException exception) when (exception.Code == ConcurrencyConflict && attempt == 1)
            {
                // Otro guardado del producto ganó: un scope nuevo relee y vuelve a intentar una vez.
            }
            catch (Exception exception) when (exception is CatalogDomainException or RequestConcurrencyException)
            {
                await ReleaseAsync(image.BuyerTenantId, stored.FileId, now, cancellationToken);
                LogRejected(logger, image.BuyerTenantId, image.ProductId);
                return;
            }
        }
    }

    private async Task<SourceImage?> ReadSourceAsync(PendingProductImage image, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var variantId = new VariantId(image.SupplierVariantId);
        var products = await services.GetRequiredService<IProductRepository>()
            .FindVariantsByIdsAsync(image.SupplierTenantId, [variantId], cancellationToken);
        var product = products.FirstOrDefault(candidate => candidate.Variants.Any(variant => variant.Id == variantId));
        var variant = product?.Variants.FirstOrDefault(candidate => candidate.Id == variantId);
        if (product is null || variant is null || CatalogVariantReads.ImageFileIdOf(product, variant) is not { } fileId)
        {
            return null;
        }

        var file = await services.GetRequiredService<IFileResourceRepository>().GetAsync(new FileResourceId(fileId), cancellationToken);
        if (file is null
            || file.TenantId != image.SupplierTenantId
            || file.Status != FileResourceStatus.Available
            || file.OwnerType != FileOwnerType.Product
            || file.SizeBytes is <= 0 or > MaxBytes)
        {
            return null;
        }

        var content = await services.GetRequiredService<IObjectStorage>().DownloadAsync(file.StorageKey, cancellationToken);
        return new SourceImage(file.MimeType, content);
    }

    private async Task ReleaseAsync(Guid tenantId, Guid fileId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProductImageReleasePublisher>().Publish(tenantId, [fileId], now);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>().SaveChangesAsync(cancellationToken);
    }

    private static string FileNameFor(Guid productId, string contentType) => contentType switch
    {
        "image/png" => $"wholesale-{productId:N}.png",
        "image/webp" => $"wholesale-{productId:N}.webp",
        _ => $"wholesale-{productId:N}.jpg",
    };

    private sealed record SourceImage(string MimeType, byte[] Content);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The wholesaler image could not be attached to catalog product {ProductId} of tenant {TenantId}; the product stays without an image.")]
    private static partial void LogRejected(ILogger logger, Guid tenantId, Guid productId);
}
