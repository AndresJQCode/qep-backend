using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

public sealed record GetBillingSettingsQuery(Guid TenantId) : IQuery<BillingSettingsDto>;

/// <summary>
/// Los ajustes del tenant (spec §4: manage, no read; sólo quien puede cambiarlos los ve) con las conexiones de WhatsApp que
/// ofrece el puerto de salida. Sin fila guardada responde la conexión en null: no hace falta crearla para leerla.
/// </summary>
public sealed class GetBillingSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBillingSettingsRepository settingsRepository,
    IBillingWhatsAppOutbound whatsApp,
    IClock clock)
    : IQueryHandler<GetBillingSettingsQuery, BillingSettingsDto>
{
    public async Task<BillingSettingsDto> HandleAsync(GetBillingSettingsQuery query, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, BillingPermissions.Manage, cancellationToken);

        var settings = await settingsRepository.GetOrDefaultAsync(query.TenantId, clock.UtcNow, cancellationToken);
        var connections = await whatsApp.ListConnectionsAsync(query.TenantId, cancellationToken);

        return new BillingSettingsDto(settings.WhatsAppConnectionId, connections);
    }
}
