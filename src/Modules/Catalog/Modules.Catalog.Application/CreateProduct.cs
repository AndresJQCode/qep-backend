using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record CreateProductCommand(Guid TenantId, string Name, string Code)
    : ICommand<ProductDto>;

// The domain enforces the same rules and would throw a 422 with a single code. The validator
// exists so the response carries the per-field errors map that ApiExceptionHandler builds
// from ValidationException, which is what a form needs to mark the offending input.
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
    }
}

public sealed class CreateProductHandler(
    IProductRepository repository,
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
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);

        var now = clock.UtcNow;
        var product = Product.Create(
            ProductId.New(), command.TenantId, command.Name, command.Code, now);

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
