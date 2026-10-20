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
    IOptionDefinitionRepository optionRepository,
    IProductImageLookup imageLookup,
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

        // El detalle completo, como el GET: la pantalla repinta lo que vuelve, y hasta las
        // variantes devolvía la ficha sin la URL de la imagen.
        var dtos = await new[] { product }.ToDtosAsync(
            imageLookup, optionRepository, command.TenantId, cancellationToken);
        return dtos[0];
    }
}
