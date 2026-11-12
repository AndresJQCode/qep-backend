using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Los ajustes del pedido del tenant, tal como los pinta la pantalla de configuración
/// (spec 2026-11-10 §6). Los tres viajan siempre, también los que nunca se guardaron.</summary>
public sealed record OrderSettingsDto(
    bool RequirePaymentProofToMarkPaid,
    bool RequireFullPaymentToInvoice,
    bool AutoArchive);

public sealed record GetOrderSettingsQuery(Guid TenantId) : IQuery<OrderSettingsDto>;

/// <summary>
/// Sin fila responde los valores del owner (<see cref="OrderSettings.Default"/>) y no la crea. Permiso de
/// settings, como el layout del Excel de pedidos, y el módulo de pedidos encendido: lo que se configura
/// es de orders.
/// </summary>
public sealed class GetOrderSettingsHandler(
    IQuotationSettingsStore store,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrderSettingsQuery, OrderSettingsDto>
{
    public async Task<OrderSettingsDto> HandleAsync(
        GetOrderSettingsQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, TenancyPermissions.SettingsRead);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Orders, cancellationToken);

        return OrderSettingsMapping.ToDto(await store.GetOrderSettingsAsync(query.TenantId, cancellationToken));
    }
}

internal static class OrderSettingsMapping
{
    public static OrderSettingsDto ToDto(OrderSettings settings) =>
        new(settings.RequirePaymentProofToMarkPaid, settings.RequireFullPaymentToInvoice, settings.AutoArchive);
}
