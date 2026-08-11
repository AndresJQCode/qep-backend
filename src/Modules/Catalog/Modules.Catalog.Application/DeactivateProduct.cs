using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record DeactivateProductCommand(Guid TenantId, Guid ProductId)
    : ICommand<ProductDto>;

// No validator: the command carries no free text. Deactivating twice is rejected by the
// aggregate, which is where that rule belongs.
public sealed class DeactivateProductHandler(
    IProductRepository repository,
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

        return product.ToDto();
    }
}
