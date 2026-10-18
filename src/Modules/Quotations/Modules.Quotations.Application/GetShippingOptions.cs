using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetShippingOptionsQuery(Guid TenantId) : IQuery<ShippingOptionsDto>;

/// <summary>Con el permiso de leer cotizaciones (D-F3) y no el de settings: quien cotiza tiene que
/// poder dibujar la entrega sin poder configurarla.</summary>
public sealed class GetShippingOptionsHandler(
    ICarrierRepository carriers,
    IQuotationSettingsStore store,
    IQuotationTaxRateLookup taxRates,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetShippingOptionsQuery, ShippingOptionsDto>
{
    public async Task<ShippingOptionsDto> HandleAsync(GetShippingOptionsQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, QuotationsPermissions.QuotationRead);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        var list = await carriers.ListAsync(query.TenantId, cancellationToken);
        var modes = await store.GetFreightChargeModesAsync(query.TenantId, cancellationToken);
        return new ShippingOptionsDto(
            [.. list
                .Where(carrier => carrier.IsActive)
                .Select(carrier => new ShippingCarrierOptionDto(
                    carrier.Id.Value, carrier.Name, carrier.SupportsCashOnDelivery))],
            await CarrierMapping.FreightTaxRateAsync(store, taxRates, query.TenantId, cancellationToken),
            CarrierMapping.ToDto(modes),
            modes.Default?.ToString());
    }
}
