using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary><c>GET …/siigo/connections/{connectionId}/settings</c> (<c>siigo.settings.manage</c>).</summary>
public sealed record GetSiigoSettingsQuery(Guid TenantId, Guid ConnectionId) : IQuery<SiigoSettingsDto>;

/// <summary>
/// Spec 2026-11-18 §5.2 y §5.3. En orden: tenant, permiso y módulo (403); la conexión de Siigo del tenant (403, nunca 404, también la
/// de otro tenant o de otro proveedor); y la configuración, que si todavía no existe esta lectura la crea vacía
/// (<c>INSERT … ON CONFLICT DO NOTHING</c>, como <c>PipelineProvisioner</c>: para no acoplar el alta de Integrations a Siigo).
/// </summary>
public sealed class GetSiigoSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoCompanyDirectory companies,
    ISiigoTaxRateDirectory taxRates,
    ISiigoMemberDirectory members) : IQueryHandler<GetSiigoSettingsQuery, SiigoSettingsDto>
{
    public async Task<SiigoSettingsDto> HandleAsync(GetSiigoSettingsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var connection = await SiigoSettingsSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);

        var settings = await SiigoSettingsSupport.SettingsAsync(settingsRepository, query.TenantId, query.ConnectionId, cancellationToken);
        return await SiigoSettingsSupport.ToDtoAsync(settings, connection, companies, taxRates, members, cancellationToken);
    }
}
