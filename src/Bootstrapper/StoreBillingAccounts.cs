using BuildingBlocks.Application;
using Modules.Companies.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Bootstrapper;

/// <summary>
/// Companies + Quotations → Store: las cuentas de cobro de la tienda. <see cref="ValidateAsync"/> aplica la comparación de
/// <c>QuotationBillingAccountResolver</c> (banco sin mayúsculas, número exacto, moneda, los tres recortados) sobre el mismo
/// <see cref="IQuotationCompanyLookup"/>, con sus mismos códigos: una cuenta que la tienda da por buena tiene que ser una que el
/// pedido acepte al «Tomar». La empresa que no existe es un 404 y la cuenta que no es de la empresa, un 422 (contrato A).
/// </summary>
internal sealed class StoreBillingAccounts(IQuotationCompanyLookup lookup, ICompanyRepository companies) : IStoreBillingAccounts
{
    public async Task<IReadOnlyList<StoreBillingAccountOption>> ListAsync(
        Guid tenantId, string? currency, CancellationToken cancellationToken)
    {
        // Sólo empresas activas: una dada de baja no cobra. Nada se ordena con la cultura del servidor.
        var active = await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken);
        return active
            .SelectMany(company => company.BankAccounts.Select(account => new StoreBillingAccountOption(
                company.Id.Value, company.Name, account.BankName, account.AccountNumber, account.Currency)))
            .Where(option => currency is null || string.Equals(option.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(option => option.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.BankName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.AccountNumber, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<StoreBillingAccount> ValidateAsync(
        Guid tenantId,
        Guid companyId,
        string bankName,
        string accountNumber,
        string currency,
        CancellationToken cancellationToken)
    {
        var company = await lookup.FindAsync(tenantId, companyId, cancellationToken);

        // El mismo código para «no existe» y «es de otro tenant»: distinguirlos confirmaría que el id existe en otro tenant.
        if (company is null)
        {
            throw new ResourceNotFoundException(
                "quotation.billing.company_not_found", $"Company '{companyId}' was not found in this tenant.");
        }

        var account = company.BankAccounts.FirstOrDefault(candidate =>
            string.Equals(candidate.BankName, bankName.Trim(), StringComparison.OrdinalIgnoreCase)
            && candidate.AccountNumber == accountNumber.Trim()
            && string.Equals(candidate.Currency, currency.Trim(), StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_not_found", "The bank account does not belong to the selected company.");
        }

        // Se guarda la cuenta como la tiene la empresa, no como la escribió el cliente HTTP.
        return new StoreBillingAccount(company.Id, account.BankName, account.AccountNumber, account.Currency.ToUpperInvariant());
    }
}
