using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §6.1: la cuenta de cobro de la tienda contra Companies. La comparación es la de
/// <c>QuotationBillingAccountResolver</c> (banco sin mayúsculas, número exacto, los dos recortados): una cuenta que Shopify da
/// por buena tiene que ser una que el pedido acepte.</summary>
internal sealed class ShopifyBillingAccounts(ICompanyRepository companies) : IShopifyBillingAccounts
{
    public async Task<ShopifyBillingAccountRef?> FindAsync(
        Guid tenantId, Guid companyId, string bank, string number, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken);
        var account = company?.BankAccounts.FirstOrDefault(candidate =>
            string.Equals(candidate.BankName, bank?.Trim(), StringComparison.OrdinalIgnoreCase)
            && candidate.AccountNumber == number?.Trim());
        return account is null
            ? null
            : new ShopifyBillingAccountRef(company!.Id.Value, account.BankName, account.AccountNumber, account.Currency);
    }
}
