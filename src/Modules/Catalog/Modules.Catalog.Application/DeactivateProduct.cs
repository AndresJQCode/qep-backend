using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record DeactivateProductCommand(Guid TenantId, Guid ProductId)
    : ICommand<ProductDto>;

// Sin validador: el comando no lleva texto libre. Desactivar dos veces lo rechaza el
// agregado, que es donde va esa regla.
public sealed class DeactivateProductHandler(
    IProductRepository repository,
    IOptionDefinitionRepository optionRepository,
    IProductImageLookup imageLookup,
    IVariantUsageChecker usageChecker,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeactivateProductCommand, ProductDto>
{
    public async Task<ProductDto> HandleAsync(
        DeactivateProductCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);

        var product = await repository.FindAsync(
            command.TenantId, new ProductId(command.ProductId), cancellationToken)
            ?? throw ProductNotFound.For(command.ProductId);

        var now = clock.UtcNow;
        product.Deactivate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.deactivated",
            product.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // El detalle completo, como el GET: la pantalla repinta lo que vuelve, y hasta las
        // variantes devolvía la ficha sin la URL de la imagen.
        var dtos = await new[] { product }.ToDtosAsync(
            imageLookup, optionRepository, usageChecker, command.TenantId, cancellationToken);
        return dtos[0];
    }
}
