using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ApiAccess.Application;

public sealed record ListApiScopesQuery(Guid TenantId) : IQuery<ApiScopeCatalogDto>;

public sealed class ListApiScopesHandler(
    ApiScopeCatalog scopeCatalog,
    IExecutionContext executionContext,
    ITenantModules tenantModules)
    : IQueryHandler<ListApiScopesQuery, ApiScopeCatalogDto>
{
    public async Task<ApiScopeCatalogDto> HandleAsync(
        ListApiScopesQuery query,
        CancellationToken cancellationToken)
    {
        await ApiAccessAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ApiAccessPermissions.ApiKeyManage, cancellationToken);

        var available = await scopeCatalog.AvailableForAsync(query.TenantId, cancellationToken);

        // Un módulo apagado no deja categoría vacía: simplemente no aparece.
        var categories = available
            .GroupBy(definition => definition.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ApiScopeCategoryDto(
                group.Key,
                group
                    .OrderBy(definition => definition.Key, StringComparer.Ordinal)
                    .Select(definition => new ApiScopeDto(
                        definition.Key,
                        definition.Name,
                        definition.Description,
                        definition.RestrictableByConnection))
                    .ToArray()))
            .ToArray();

        return new ApiScopeCatalogDto(categories);
    }
}
