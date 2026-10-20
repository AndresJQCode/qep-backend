using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record CreateProductCommand(
    Guid TenantId,
    string Name,
    string? Description,
    Guid? TaxRateId,
    IReadOnlyList<Guid>? OptionIds,
    IReadOnlyList<ProductVariantRequest>? Variants,
    IReadOnlyList<Guid>? Gallery) : ICommand<ProductDto>, IProductWriteCommand;

// Las reglas viven en ProductWriteRules y se incluyen, no se copian: duplicarlas entre este
// validador y el del PUT fue el hallazgo `D` de la revisión de 4 lentes. En el POST el precio de
// cada variante es obligatorio: no hay variante guardada de la cual heredarlo.
public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        Include(new ProductWriteRules(pricingRequired: true));
    }
}

public sealed class CreateProductHandler(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
    IOptionDefinitionRepository optionRepository,
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

        // CAT-05: cada foto es referencia blanda a Storage, sin FK, así que esta comprobación es
        // la única red. Ver ProductImageResolver.
        var gallery = command.Gallery ?? [];
        await ProductImageResolver.ResolveGalleryAsync(
            imageLookup, command.TenantId, gallery, cancellationToken);

        var optionIds = command.OptionIds ?? [];
        var library = await ProductOptionLibrary.LoadAsync(
            optionRepository, command.TenantId, optionIds, cancellationToken);

        // En el POST no hay nada que proyectar ni heredar: cada variante trae sus valores y su
        // precio (el validador ya exigió el precio).
        var variants = command.Variants!
            .Select(variant => new ProductVariantInput
            {
                Code = variant.Code ?? string.Empty,
                Barcode = variant.Barcode,
                OptionValueIds = variant.OptionValueIds ?? [],
                Pricing = variant.Pricing!.ToDomain(),
                GalleryFileId = variant.GalleryFileId,
                IsActive = variant.IsActive ?? true,
            })
            .ToArray();

        ProductVariantReferences.EnsureValid(null, optionIds, variants, library);
        await ProductVariantReferences.EnsureCodesAvailableAsync(
            repository, command.TenantId, null, variants, cancellationToken);

        var now = clock.UtcNow;
        var product = Product.Create(
            ProductId.New(),
            command.TenantId,
            command.Name,
            new ProductDetails
            {
                Description = command.Description,
                TaxRateId = taxRateId
            },
            optionIds,
            variants,
            gallery,
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

        var dtos = await new[] { product }.ToDtosAsync(
            imageLookup, optionRepository, command.TenantId, cancellationToken);
        return dtos[0];
    }
}
