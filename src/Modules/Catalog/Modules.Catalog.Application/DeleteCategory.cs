using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <param name="ExpectedVersion">From <c>If-Match</c>; the endpoint answers 428 without it.</param>
public sealed record DeleteCategoryCommand(Guid TenantId, Guid CategoryId, long ExpectedVersion)
    : ICommand<CategoryDeletedResult>;

// The endpoint answers 204 and does not look at it; same shape as TaxRateDeletedResult.
public sealed record CategoryDeletedResult(bool Deleted);

/// <summary>
/// Deletes a category **only if nothing hangs from it** (spec 2026-10-26-categorias-pos-acceso-rapido,
/// D-2). Both FKs —products and child categories— are already RESTRICT; the handler checks first
/// so the caller gets a 422 with a code it can explain, as <see cref="DeleteTaxRateHandler"/> does.
/// The FKs stay as the last line of defence for the race, and <see cref="ICatalogUnitOfWork"/>
/// translates them to the same codes.
/// </summary>
public sealed class DeleteCategoryHandler(
    IProductCategoryRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteCategoryCommand, CategoryDeletedResult>
{
    public async Task<CategoryDeletedResult> HandleAsync(
        DeleteCategoryCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);

        var categoryId = new ProductCategoryId(command.CategoryId);
        var tree = await repository.ListForUpdateAsync(command.TenantId, cancellationToken);
        var category = tree.SingleOrDefault(candidate => candidate.Id == categoryId)
            ?? throw CategoryNotFound.For(command.CategoryId);

        if (category.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The category changed after it was loaded.");
        }

        // Any product, active or inactive: an inactive one still holds the FK.
        if (await repository.CountProductsAsync(command.TenantId, categoryId, cancellationToken) > 0)
        {
            throw CategoryInUse.HasProducts();
        }

        if (tree.Any(candidate => candidate.ParentId == categoryId))
        {
            throw CategoryInUse.HasChildren();
        }

        // A pinned category leaves the register's chips and the rest close the gap.
        if (category.PosQuickAccessPosition is not null)
        {
            ProductCategoryQuickAccess.Remove(tree, categoryId);
        }

        var now = clock.UtcNow;
        repository.Remove(category);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.category.deleted",
            category.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CategoryDeletedResult(true);
    }
}
