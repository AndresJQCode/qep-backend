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
    decimal? Price,
    string? Currency,
    Guid? TaxRateId) : ICommand<ProductDto>;

// El dominio hace cumplir las mismas reglas y tiraría un 422 con un solo código. El validador
// existe para que la respuesta lleve el mapa de errores por campo que ApiExceptionHandler arma
// desde ValidationException, que es lo que un formulario necesita para marcar el input culpable.
public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(Product.NameMaxLength);
        RuleFor(command => command.Code)
            .NotEmpty()
            .MaximumLength(Product.CodeMaxLength);
        RuleFor(command => command.Description)
            .MaximumLength(ProductDetails.DescriptionMaxLength);
        RuleFor(command => command.Price)
            .GreaterThanOrEqualTo(0m)
            .When(command => command.Price.HasValue);
        RuleFor(command => command.Currency)
            .Length(ProductDetails.CurrencyLength)
            .When(command => !string.IsNullOrWhiteSpace(command.Currency));
    }
}

public sealed class CreateProductHandler(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
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

        var now = clock.UtcNow;
        var product = Product.Create(
            ProductId.New(),
            command.TenantId,
            command.Name,
            command.Code,
            new ProductDetails(
                command.Description,
                command.ImageFileId,
                command.Price,
                command.Currency,
                taxRateId),
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

        return product.ToDto();
    }
}
