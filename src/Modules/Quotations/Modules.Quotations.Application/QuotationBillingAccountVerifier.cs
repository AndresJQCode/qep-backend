using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>La cuenta tal como la tiene la empresa hoy (no como la escribió quien llamó), con el país, el municipio y el
/// IVA de la empresa, que el documento congela con ella. Banco y número nulos los dos es «empresa sin cuenta» (spec
/// 2026-11-17, R3).</summary>
public sealed record VerifiedBillingAccount(
    Guid CompanyId,
    string? BankName,
    string? AccountNumber,
    string Currency,
    string Country,
    Guid? CityId = null,
    bool ChargesVat = true)
{
    public bool HasBankAccount => BankName is not null;
}

/// <summary>
/// La ÚNICA comprobación de que una cuenta de cobro es de la empresa y del tenant: moneda del catálogo primero, empresa del
/// tenant después, y la cuenta por banco sin mayúsculas, número exacto y moneda, los tres recortados. La usan el resolver de las
/// cotizaciones y los pedidos y el adaptador de la tienda pública, así que una cuenta que la tienda acepta es, por construcción,
/// una que el pedido acepta al «Tomar». Lanza <see cref="QuotationsDomainException"/> con
/// <see cref="CompanyNotFoundCode"/> o <see cref="AccountNotFoundCode"/>.
///
/// Sin banco ni número (los dos en blanco) no busca cuenta: es una empresa sin cuenta (spec 2026-11-17 §4.2), y la moneda
/// que devuelve es la que pasó quien llama, porque ya no sale de una cuenta.
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

        if (string.IsNullOrWhiteSpace(bankName) && string.IsNullOrWhiteSpace(accountNumber))
        {
            return new VerifiedBillingAccount(
                company.Id, null, null, normalizedCurrency, company.Country, company.CityId, company.ChargesVat);
        }

        var account = company.BankAccounts.FirstOrDefault(candidate =>
            string.Equals(candidate.BankName, bankName?.Trim(), StringComparison.OrdinalIgnoreCase)
            && candidate.AccountNumber == accountNumber?.Trim()
            && string.Equals(candidate.Currency, normalizedCurrency, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            throw new QuotationsDomainException(AccountNotFoundCode, "The bank account does not belong to the selected company.");
        }

        return new VerifiedBillingAccount(
            company.Id,
            account.BankName,
            account.AccountNumber,
            normalizedCurrency,
            company.Country,
            company.CityId,
            company.ChargesVat);
    }
}
