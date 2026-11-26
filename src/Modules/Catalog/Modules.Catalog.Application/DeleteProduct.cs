using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <param name="ExpectedVersion">De <c>If-Match</c>; sin él el endpoint responde 428.</param>
public sealed record DeleteProductCommand(Guid TenantId, Guid ProductId, long ExpectedVersion)
    : ICommand<ProductDeletedResult>;

// El endpoint responde 204 y no lo mira; misma forma que CategoryDeletedResult.
public sealed record ProductDeletedResult(bool Deleted);

/// <summary>
/// Borra el producto para siempre, aunque esté en uso (spec 2026-11-08, D-5): las líneas de cotizaciones, pedidos y la
/// caja se quedan con su código y su nombre congelados, los borradores y las solicitudes nuevas de la tienda pierden la
/// línea, y las fotos de la galería se liberan para que Storage las purgue. Todo en **una** transacción: si un paso
/// falla, no queda nada escrito, outbox incluido.
/// </summary>
public sealed class DeleteProductHandler(
    IProductRepository repository,
    IProductDeletionCleanup cleanup,
    IProductImageReleasePublisher imageReleasePublisher,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteProductCommand, ProductDeletedResult>
{
    public async Task<ProductDeletedResult> HandleAsync(
        DeleteProductCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);

        var product = await repository.FindAsync(
            command.TenantId, new ProductId(command.ProductId), cancellationToken)
            ?? throw ProductNotFound.For(command.ProductId);

        // El mismo contrato que el PUT: quien borra lo que leyó, no lo que otro cambió después. El token de concurrencia
        // de la fila cubre la carrera entre esta lectura y el commit (CatalogUnitOfWork la traduce a 412).
        if (product.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The product changed after it was loaded.");
        }

        // Todas las variantes, inactivas incluidas: una desactivada también puede estar en una cotización o una venta.
        var variantIds = product.Variants.Select(variant => variant.Id.Value).ToArray();
        var fileIds = product.Gallery.Select(image => image.FileId).Distinct().ToArray();
        var now = clock.UtcNow;

        // La limpieza va dentro de la transacción y antes del Remove: lee el código y el nombre de las variantes del
        // catálogo para congelarlos en las líneas que se quedan.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await cleanup.ApplyAsync(command.TenantId, variantIds, now, cancellationToken);

        repository.Remove(product);
        imageReleasePublisher.Publish(command.TenantId, fileIds, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.deleted",
            product.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ProductDeletedResult(true);
    }
}
