using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

public sealed record ListSupplierCategoriesQuery(Guid TenantId) : IQuery<IReadOnlyList<string>>;

public sealed class ListSupplierCategoriesHandler(
    ISupplierRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListSupplierCategoriesQuery, IReadOnlyList<string>>
{
    /// <summary>Tope de categorías devueltas (spec §6.1): alimentan un filtro y unas sugerencias, no un catálogo.</summary>
    public const int MaxCategories = 200;

    public Task<IReadOnlyList<string>> HandleAsync(
        ListSupplierCategoriesQuery query,
        CancellationToken cancellationToken)
    {
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, SuppliersPermissions.Read);

        return repository.ListCategoriesAsync(query.TenantId, MaxCategories, cancellationToken);
    }
}
