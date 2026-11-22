using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetShippingOptionsQuery(Guid TenantId) : IQuery<ShippingOptionsDto>;

/// <summary>Con el permiso de leer cotizaciones (D-F3) o el de leer pedidos, y no el de settings: quien cotiza o
/// registra un pedido tiene que poder dibujar la entrega sin poder configurarla. El de pedidos existe porque un
/// tenant puede ocultar cotizaciones: ahí <c>QuotationRead</c> lo enmascara el módulo y sólo queda <c>OrderRead</c>.</summary>
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
        QuotationsAuthorization.EnsureAuthorizedAny(
            executionContext, query.TenantId, QuotationsPermissions.QuotationRead, OrdersPermissions.OrderRead);
        await TenantModuleGuard.EnsureAnyEnabledAsync(
            tenantModules, query.TenantId, QuotationsModuleSets.QuotationsOrOrders, cancellationToken);

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
