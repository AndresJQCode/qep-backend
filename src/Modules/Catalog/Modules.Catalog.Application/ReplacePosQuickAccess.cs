using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <param name="CategoryIds">The order is the position: index 0 → 1. Empty = nothing pinned.</param>
public sealed record ReplacePosQuickAccessCommand(Guid TenantId, IReadOnlyList<Guid>? CategoryIds)
    : ICommand<IReadOnlyList<CategoryDto>>;

// Only the shape: duplicates and unknown ids are domain rules with their own codes
// (ProductCategoryQuickAccess.Replace).
public sealed class ReplacePosQuickAccessValidator : AbstractValidator<ReplacePosQuickAccessCommand>
{
    public ReplacePosQuickAccessValidator()
    {
        RuleFor(command => command.CategoryIds).NotNull();
    }
}

/// <summary>
/// Replaces the tenant's whole quick-access list (spec 2026-10-26-categorias-pos-acceso-rapido, D-1,
/// D-4). No <c>If-Match</c>: it is a whole-list replace and the last write wins. Positions do not
/// bump any category's <c>Version</c> (D-5). Answers the full category list so the screen
/// refreshes in one go.
/// </summary>
public sealed class ReplacePosQuickAccessHandler(
    IProductCategoryRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ReplacePosQuickAccessCommand> validator)
    : ICommandHandler<ReplacePosQuickAccessCommand, IReadOnlyList<CategoryDto>>
{
    public async Task<IReadOnlyList<CategoryDto>> HandleAsync(
        ReplacePosQuickAccessCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var tree = await repository.ListForUpdateAsync(command.TenantId, cancellationToken);
        ProductCategoryQuickAccess.Replace(
            tree,
            command.CategoryIds!.Select(id => new ProductCategoryId(id)).ToList());

        var now = clock.UtcNow;
        // The resource is the tenant's list, not one category.
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.category.pos_quick_access_replaced",
            command.TenantId.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Same read as GET /catalog/categories: direct products, active and inactive.
        var items = await repository.ListAsync(
            command.TenantId, onlyActiveProducts: false, cancellationToken);
        return items.Select(item => item.ToDto()).ToArray();
    }
}
