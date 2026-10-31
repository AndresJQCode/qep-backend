using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ApiAccess.Application;

public sealed record ListApiKeysQuery(Guid TenantId) : IQuery<IReadOnlyList<ApiKeyDto>>;

public sealed class ListApiKeysHandler(
    IApiKeyRepository repository,
    IApiKeyConnectionDirectory connections,
    ApiScopeCatalog scopeCatalog,
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IClock clock)
    : IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>>
{
    public async Task<IReadOnlyList<ApiKeyDto>> HandleAsync(
        ListApiKeysQuery query,
        CancellationToken cancellationToken)
    {
        await ApiAccessAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ApiAccessPermissions.ApiKeyManage, cancellationToken);

        var keys = await repository.ListAsync(query.TenantId, cancellationToken);
        var names = await connections.ListAsync(query.TenantId, cancellationToken);
        var now = clock.UtcNow;

        return keys.Select(key => key.ToDto(scopeCatalog.All, names, now)).ToArray();
    }
}
