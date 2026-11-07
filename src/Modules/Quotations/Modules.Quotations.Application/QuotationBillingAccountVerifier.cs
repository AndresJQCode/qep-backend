using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>La cuenta tal como la tiene la empresa hoy (no como la escribió quien llamó).</summary>
public sealed record VerifiedBillingAccount(Guid CompanyId, string BankName, string AccountNumber, string Currency);

/// <summary>
/// La ÚNICA comprobación de que una cuenta de cobro es de la empresa y del tenant: moneda del catálogo primero, empresa del
/// tenant después, y la cuenta por banco sin mayúsculas, número exacto y moneda, los tres recortados. La usan el resolver de las
/// cotizaciones y los pedidos y el adaptador de la tienda pública, así que una cuenta que la tienda acepta es, por construcción,
/// una que el pedido acepta al «Tomar». Lanza <see cref="QuotationsDomainException"/> con
/// <see cref="CompanyNotFoundCode"/> o <see cref="AccountNotFoundCode"/>.
/// </summary>
public static class QuotationBillingAccountVerifier
{
    public const string CompanyNotFoundCode = "quotation.billing.company_not_found";
    public const string AccountNotFoundCode = "quotation.billing.account_not_found";

    public static async Task<VerifiedBillingAccount> VerifyAsync(
        IQuotationCompanyLookup lookup,
        Guid tenantId,
        Guid companyId,
        string? bankName,
        string? accountNumber,
        string? currency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        // Before the company lookup: a currency outside the catalogue can never be quoted, whatever
        // the company has on file (spec D6; replaces the former Quotations-only currency code).
        var normalizedCurrency = Currencies.Normalize(currency);

        var company = await lookup.FindAsync(tenantId, companyId, cancellationToken);

        // Mismo código para "no existe" y "es de otro tenant", igual que con el cliente:
        // distinguirlos confirmaría que el id existe en otro tenant.
        if (company is null)
        {
            throw new QuotationsDomainException(CompanyNotFoundCode, $"Company '{companyId}' was not found in this tenant.");
        }

        var account = company.BankAccounts.FirstOrDefault(candidate =>
            string.Equals(candidate.BankName, bankName?.Trim(), StringComparison.OrdinalIgnoreCase)
            && candidate.AccountNumber == accountNumber?.Trim()
            && string.Equals(candidate.Currency, normalizedCurrency, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            throw new QuotationsDomainException(AccountNotFoundCode, "The bank account does not belong to the selected company.");
        }

        return new VerifiedBillingAccount(company.Id, account.BankName, account.AccountNumber, normalizedCurrency);
    }
}
