using BuildingBlocks.Application;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

public sealed record GetServiceQuery(Guid TenantId, Guid ServiceId) : IQuery<ServiceDetailDto>;

public sealed class GetServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBillingServiceRepository services,
    ServiceDetailComposer composer)
    : IQueryHandler<GetServiceQuery, ServiceDetailDto>
{
    public async Task<ServiceDetailDto> HandleAsync(GetServiceQuery query, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, BillingPermissions.Read, cancellationToken);

        // Acotado al tenant de la ruta: el servicio de otro tenant no es «no tuyo», es «no existe».
        var service = await services.FindAsync(query.TenantId, new BillingServiceId(query.ServiceId), cancellationToken)
            ?? throw BillingNotFound.Service(query.ServiceId);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}
