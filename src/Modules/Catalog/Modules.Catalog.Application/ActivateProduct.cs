using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record ActivateProductCommand(Guid TenantId, Guid ProductId)
    : ICommand<ProductDto>;

// Sin validador, por la misma razón que DeactivateProduct: el comando no lleva texto libre.
// Activar algo ya activo lo rechaza el agregado, que es donde vive esa regla.
public sealed class ActivateProductHandler(
    IProductRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ActivateProductCommand, ProductDto>
{
    public async Task<ProductDto> HandleAsync(
        ActivateProductCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio, no después. La revisión de CAT-02 ya corrigió
        // ese orden una vez: consultar primero le confirma al llamador que el id existe.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);

        var product = await repository.FindAsync(
            command.TenantId, new ProductId(command.ProductId), cancellationToken)
            ?? throw ProductNotFound.For(command.ProductId);

        var now = clock.UtcNow;
        product.Activate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.activated",
            product.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return product.ToDto();
    }
}
