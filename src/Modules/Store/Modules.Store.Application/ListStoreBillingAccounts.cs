using BuildingBlocks.Application;
using Modules.Store.Domain;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

/// <summary>
/// Las cuentas de cobro que el administrador de la tienda puede elegir. Pide <c>store.settings.manage</c> y no el permiso de empresas:
/// quien configura la tienda tiene que poder llenar el selector sin ver el módulo de empresas.
/// </summary>
public sealed record ListStoreBillingAccountsQuery(Guid TenantId, string? Currency) : IQuery<StoreBillingAccountListDto>;

public sealed class ListStoreBillingAccountsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IStoreBillingAccounts billingAccounts)
    : IQueryHandler<ListStoreBillingAccountsQuery, StoreBillingAccountListDto>
{
    public async Task<StoreBillingAccountListDto> HandleAsync(
        ListStoreBillingAccountsQuery query, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, StorePermissions.SettingsManage, cancellationToken);

        string? currency = null;
        if (!string.IsNullOrWhiteSpace(query.Currency))
        {
            currency = query.Currency.Trim();
            if (!OnlineStore.IsCurrencyShape(currency))
            {
                throw new StoreDomainException(StoreErrorCodes.CurrencyInvalid, "La moneda debe ser de 3 letras.");
            }

            currency = currency.ToUpperInvariant();
        }

        return new StoreBillingAccountListDto(await billingAccounts.ListAsync(query.TenantId, currency, cancellationToken));
    }
}
