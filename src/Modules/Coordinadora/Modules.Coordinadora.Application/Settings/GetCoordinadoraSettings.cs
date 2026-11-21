using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Settings;

/// <summary><c>GET …/coordinadora/connections/{connectionId}/settings</c> (<c>coordinadora.settings.manage</c>).</summary>
public sealed record GetCoordinadoraSettingsQuery(Guid TenantId, Guid ConnectionId) : IQuery<CoordinadoraSettingsDto>;

/// <summary>
/// Spec 2026-11-21 §5.2. En orden: tenant, permiso y módulo (403); la conexión de Coordinadora del tenant (403, nunca 404, también la de
/// otro tenant o de otro proveedor); y la configuración, que si todavía no existe esta lectura la crea vacía (<c>INSERT … ON CONFLICT DO
/// NOTHING</c>, como Siigo: para no acoplar el alta de Integrations a este módulo).
/// </summary>
public sealed class GetCoordinadoraSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraSettingsRepository settingsRepository,
    ICoordinadoraGeographyLookup geography) : IQueryHandler<GetCoordinadoraSettingsQuery, CoordinadoraSettingsDto>
{
    public async Task<CoordinadoraSettingsDto> HandleAsync(GetCoordinadoraSettingsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await CoordinadoraSettingsSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var connection = await CoordinadoraSettingsSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);
        var settings = await CoordinadoraSettingsSupport.SettingsAsync(settingsRepository, query.TenantId, query.ConnectionId, cancellationToken);
        return await CoordinadoraSettingsSupport.ToDtoAsync(settings, connection, geography, cancellationToken);
    }
}
