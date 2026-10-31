using BuildingBlocks.Application;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public sealed record GetServiceQuery(Guid TenantId, Guid ServiceId) : IQuery<ServiceDetailDto>;

public sealed class GetServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractRepository services,
    ServiceDetailComposer composer)
    : IQueryHandler<GetServiceQuery, ServiceDetailDto>
{
    public async Task<ServiceDetailDto> HandleAsync(GetServiceQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Read, cancellationToken);

        // Acotado al tenant de la ruta: el servicio de otro tenant no es «no tuyo», es «no existe».
        var service = await services.FindAsync(query.TenantId, new ServiceContractId(query.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(query.ServiceId);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}
