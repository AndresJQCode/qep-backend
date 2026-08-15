using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record UpdateProductCommand(
    Guid TenantId,
    Guid ProductId,
    string Name,
    string Code,
    string? Description,
    Guid? ImageFileId,
    decimal? Price,
    string? Currency,
    Guid? TaxRateId) : ICommand<ProductDto>, IProductWriteCommand;

// Mismas reglas que el POST, por inclusión y no por copia. Ver ProductWriteRules.
public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator() => Include(new ProductWriteRules());
}

public sealed class UpdateProductHandler(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
    IProductImageLookup imageLookup,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateProductCommand> validator)
    : ICommandHandler<UpdateProductCommand, ProductDto>
{
    public async Task<ProductDto> HandleAsync(
        UpdateProductCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razón en CreateProductHandler.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var product = await repository.FindAsync(
            command.TenantId, new ProductId(command.ProductId), cancellationToken)
            ?? throw ProductNotFound.For(command.ProductId);

        var taxRateId = await ProductTaxRateResolver.ResolveAsync(
            taxRateRepository, command.TenantId, command.TaxRateId, cancellationToken);

        // CAT-05, igual que en el POST: sin esto un PUT puede mover la portada de un producto a
        // un archivo de otro tenant, que es la mitad del criterio CA-CAT-05-01.
        var image = await ProductImageResolver.ResolveAsync(
            imageLookup, command.TenantId, command.ImageFileId, cancellationToken);

        var now = clock.UtcNow;

        // Los cinco campos se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, así que un campo ausente se limpia. Es lo que verifica CA-CAT-04-03.
        product.Update(
            command.Name,
            command.Code,
            new ProductDetails
            {
                Description = command.Description,
                ImageFileId = image?.FileId,
                Price = command.Price,
                Currency = command.Currency,
                TaxRateId = taxRateId
            },
            now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.updated",
            product.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return product.ToDto(image?.PublicUrl);
    }
}
