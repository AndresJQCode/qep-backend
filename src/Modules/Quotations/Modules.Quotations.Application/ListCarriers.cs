using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ListCarriersQuery(Guid TenantId) : IQuery<CarrierSettingsDto>;

/// <summary>La pantalla de transportadoras entera (spec 2026-10-17 §4.3), con los permisos de
/// settings como el resto de la configuración de Quotations (H13).</summary>
public sealed class ListCarriersHandler(
    ICarrierRepository carriers,
    IQuotationSettingsStore store,
    IQuotationTaxRateLookup taxRates,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<ListCarriersQuery, CarrierSettingsDto>
{
    public async Task<CarrierSettingsDto> HandleAsync(ListCarriersQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, TenancyPermissions.SettingsRead);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        return await CarrierMapping.SettingsAsync(carriers, store, taxRates, query.TenantId, cancellationToken);
    }
}
