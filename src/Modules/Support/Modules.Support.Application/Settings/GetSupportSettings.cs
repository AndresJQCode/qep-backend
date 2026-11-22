using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record GetSupportSettingsQuery(Guid TenantId) : IQuery<SupportSettingsDto>;

/// <summary>Lo leen leer, gestionar o configurar (<see cref="SupportPolicies.SettingsView"/>): quien configura lee la versión que
/// tiene que mandar al guardar, y quien gestiona ve el umbral con que la bandeja marca «por vencer».</summary>
public sealed class GetSupportSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportProvisioner provisioner,
    ISupportSettingsRepository settings,
    SupportSettingsComposer composer)
    : IQueryHandler<GetSupportSettingsQuery, SupportSettingsDto>
{
    public async Task<SupportSettingsDto> HandleAsync(GetSupportSettingsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);

        return await composer.ComposeAsync(await settings.GetAsync(query.TenantId, cancellationToken), cancellationToken);
    }
}
