using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record CreateProductCommand(
    Guid TenantId,
    string Name,
    string Code,
    string? Description,
    Guid? ImageFileId,
    string? Currency,
    Guid? TaxRateId,
    ProductPricingRequest Pricing) : ICommand<ProductDto>, IProductWriteCommand;

// Las reglas viven en ProductWriteRules y se incluyen, no se copian: duplicarlas entre este
// validador y el del PUT fue el hallazgo `D` de la revisión de 4 lentes. CAT-09: el precio y
// las escalas siguen el mismo criterio, con su propio ProductPricingRules.
public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        Include(new ProductWriteRules());
        RuleFor(command => command.Pricing).SetValidator(new ProductPricingRules());
    }
}

public sealed class CreateProductHandler(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
    IProductImageLookup imageLookup,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateProductCommand> validator)
    : ICommandHandler<CreateProductCommand, ProductDto>
{
    public async Task<ProductDto> HandleAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, y no al revés. La política del endpoint ya frena a quien
        // le falta el permiso, pero no al que lo tiene para otro tenant: a ése lo rechaza esta
        // revalidación. Validando primero, ese llamador ajeno se lleva el mapa de errores por
        // campo —la forma del contrato— antes de que nadie le diga que no. Lo encontró la
        // revisión de riesgo de CAT-02.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Antes de construir el agregado: la FK garantiza que la tasa exista, no que sea de este
        // tenant. Ver ProductTaxRateResolver.
        var taxRateId = await ProductTaxRateResolver.ResolveAsync(
            taxRateRepository, command.TenantId, command.TaxRateId, cancellationToken);

        // CAT-05: la imagen no tiene FK que la respalde —es referencia blanda a Storage—, así
        // que esta comprobación es la única red. Ver ProductImageResolver.
        var image = await ProductImageResolver.ResolveAsync(
            imageLookup, command.TenantId, command.ImageFileId, cancellationToken);

        var now = clock.UtcNow;
        var product = Product.Create(
            ProductId.New(),
            command.TenantId,
            command.Name,
            command.Code,
            new ProductDetails
            {
                Description = command.Description,
                ImageFileId = image?.FileId,
                Currency = command.Currency,
                TaxRateId = taxRateId
            },
            command.Pricing.ToDomain(),
            now);

        repository.Add(product);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.created",
            product.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return product.ToDto(image?.PublicUrl);
    }
}
