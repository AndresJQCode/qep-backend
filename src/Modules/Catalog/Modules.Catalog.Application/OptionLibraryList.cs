using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record ListOptionsQuery(Guid TenantId) : IQuery<OptionsResponse>;

public sealed class ListOptionsHandler(
    IOptionDefinitionRepository repository,
    IOptionLibraryProvisioner provisioner,
    IExecutionContext executionContext)
    : IQueryHandler<ListOptionsQuery, OptionsResponse>
{
    public async Task<OptionsResponse> HandleAsync(
        ListOptionsQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de sembrar: quien no es del tenant no le crea filas a nadie.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.ProductRead);

        // Spec §3.1: las predefinidas nacen en el primer acceso. Leer siembra a propósito, como
        // el tablero de Pipeline; quien sólo lee también ve la biblioteca completa.
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);

        var options = await repository.ListAsync(query.TenantId, cancellationToken);
        var usage = await repository.GetUsageAsync(query.TenantId, cancellationToken);

        return new OptionsResponse(options.Select(option => option.ToResponse(usage)).ToArray());
    }
}
