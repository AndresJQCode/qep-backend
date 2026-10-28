using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record CreateCategoryCommand(Guid TenantId, string Name, Guid? ParentId)
    : ICommand<CategoryDto>;

// El dominio hace cumplir las mismas reglas con un solo código; el validador existe para que el
// 422 lleve el mapa de errores por campo. El largo se mide después de recortar, como el dominio.
// Los mensajes van en español porque la pantalla de categorías los muestra tal cual.
public sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator()
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

public sealed class CreateCategoryHandler(
    IProductCategoryRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCategoryCommand> validator)
    : ICommandHandler<CreateCategoryCommand, CategoryDto>
{
    public async Task<CategoryDto> HandleAsync(
        CreateCategoryCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: quien tiene el permiso en otro tenant no recibe el mapa de
        // errores por campo antes de que le digan que no.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        ProductCategoryId? parentId = command.ParentId is { } parent
            ? new ProductCategoryId(parent)
            : null;
        var tree = await repository.ListTreeAsync(command.TenantId, cancellationToken);
        ProductCategoryTree.EnsureCanNestUnder(tree, parentId);

        var now = clock.UtcNow;
        var category = ProductCategory.Create(
            ProductCategoryId.New(), command.TenantId, command.Name, parentId, now);

        repository.Add(category);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.category.created",
            category.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return category.ToDto(productCount: 0);
    }
}
