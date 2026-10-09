using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetQuotationSettingsQuery(Guid TenantId) : IQuery<QuotationSettingsDto>;

public sealed class GetQuotationSettingsHandler(
    IQuotationSettingsStore store,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetQuotationSettingsQuery, QuotationSettingsDto>
{
    public async Task<QuotationSettingsDto> HandleAsync(
        GetQuotationSettingsQuery query, CancellationToken cancellationToken)
    {
        // Settings permissions, like the orders export layout (OrdersExportLayoutEndpoints).
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, TenancyPermissions.SettingsRead);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        return QuotationSettingsMapping.ToDto(await store.GetAsync(query.TenantId, cancellationToken));
    }
}

internal static class QuotationSettingsMapping
{
    // Catalogue order, like every per-currency map the frontend paints.
    public static QuotationSettingsDto ToDto(QuotationSettings settings) => new(
        settings.MinimumUnits,
        settings.MinimumTotals
            .OrderBy(entry => Currencies.OrderOf(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value));
}
