using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

public sealed record ListOwnersQuery(Guid TenantId) : IQuery<OwnersDto>;

/// <summary>Los dueños del tenant, el Self primero. El GET escribe sólo a través del provisioner (P-8): jamás crea el Self por su
/// cuenta, para que haya un único escritor.</summary>
public sealed class ListOwnersHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IOwnerRepository owners)
    : IQueryHandler<ListOwnersQuery, OwnersDto>
{
    public async Task<OwnersDto> HandleAsync(ListOwnersQuery query, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.DocumentRead, ct: cancellationToken);
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);

        var found = await owners.ListAsync(query.TenantId, cancellationToken);

        return new OwnersDto(found.Select(OwnerDtoMapper.ToDto).ToList());
    }
}
