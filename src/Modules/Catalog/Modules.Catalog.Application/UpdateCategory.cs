using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <param name="ExpectedVersion">From <c>If-Match</c>; the endpoint answers 428 without it.</param>
public sealed record UpdateCategoryCommand(
    Guid TenantId,
    Guid CategoryId,
    string Name,
    Guid? ParentId,
    long ExpectedVersion) : ICommand<CategoryDto>;

// Same rules and codes as CreateCategoryValidator: the domain enforces them too, the validator is
// what puts the per-field errors map on the 422.
public sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator()
    {
        RuleFor(command => command.Name)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithErrorCode("catalog.category.name_required")
            .WithMessage("Ingresa un nombre para la categoría.")
            .Must(name => name.Trim().Length <= ProductCategory.NameMaxLength)
            .WithErrorCode("catalog.category.name_too_long")
            .WithMessage($"El nombre es demasiado largo. Usa hasta {ProductCategory.NameMaxLength} caracteres.");
    }
}

/// <summary>
/// Rename and/or move a category with its whole subtree (spec 2026-10-26-categorias-pos-acceso-rapido).
/// A same-name sibling under the destination is caught by the unique index and comes back as
/// <c>catalog.category.name_taken</c> from <see cref="ICatalogUnitOfWork"/>.
/// </summary>
public sealed class UpdateCategoryHandler(
    IProductCategoryRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCategoryCommand> validator)
    : ICommandHandler<UpdateCategoryCommand, CategoryDto>
{
    public async Task<CategoryDto> HandleAsync(
        UpdateCategoryCommand command,
        CancellationToken cancellationToken)
    {
        // Authorize before validating, as in CreateCategoryHandler.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var categoryId = new ProductCategoryId(command.CategoryId);
        var tree = await repository.ListForUpdateAsync(command.TenantId, cancellationToken);
        var category = tree.SingleOrDefault(candidate => candidate.Id == categoryId)
            ?? throw CategoryNotFound.For(command.CategoryId);

        // Same contract as the product PUT: the row's concurrency token covers the race left
        // between this read and the commit.
        if (category.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The category changed after it was loaded.");
        }

        ProductCategoryId? parentId = command.ParentId is { } parent
            ? new ProductCategoryId(parent)
            : null;
        ProductCategoryTree.EnsureCanMove(tree, categoryId, parentId);

        var now = clock.UtcNow;
        category.Update(command.Name, parentId, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.category.updated",
            category.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var productCount = await repository.CountProductsAsync(
            command.TenantId, categoryId, cancellationToken);
        return category.ToDto(productCount);
    }
}
