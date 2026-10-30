using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public sealed record GetServiceContractsSettingsQuery(Guid TenantId) : IQuery<ServiceContractsSettingsDto>;

/// <summary>
/// Los ajustes del tenant (spec §4: manage, no read; sólo quien puede cambiarlos los ve) con las conexiones de WhatsApp que
/// ofrece el puerto de salida. Sin fila guardada responde la conexión en null: no hace falta crearla para leerla.
/// </summary>
public sealed class GetServiceContractsSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractsSettingsRepository settingsRepository,
    IServiceContractsWhatsAppOutbound whatsApp,
    IClock clock)
    : IQueryHandler<GetServiceContractsSettingsQuery, ServiceContractsSettingsDto>
{
    public async Task<ServiceContractsSettingsDto> HandleAsync(GetServiceContractsSettingsQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Manage, cancellationToken);

        var settings = await settingsRepository.GetOrDefaultAsync(query.TenantId, clock.UtcNow, cancellationToken);
        var connections = await whatsApp.ListConnectionsAsync(query.TenantId, cancellationToken);

        return new ServiceContractsSettingsDto(settings.WhatsAppConnectionId, connections);
    }
}
