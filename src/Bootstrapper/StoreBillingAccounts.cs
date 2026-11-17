using BuildingBlocks.Application;
using Modules.Companies.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Bootstrapper;

/// <summary>
/// Companies + Quotations → Store: las cuentas de cobro de la tienda. <see cref="ValidateAsync"/> aplica la comparación de
/// <c>QuotationBillingAccountVerifier</c>, la misma llamada que hace <c>QuotationBillingAccountResolver</c>: una cuenta que la
/// tienda da por buena es una que el pedido acepta al «Tomar» por construcción, no por copia. La empresa que no existe es un 404 y la cuenta que no es de la empresa, un 422 (contrato A).
/// </summary>
internal sealed class StoreBillingAccounts(IQuotationCompanyLookup lookup, ICompanyRepository companies) : IStoreBillingAccounts
{
    public async Task<IReadOnlyList<StoreBillingAccountOption>> ListAsync(
        Guid tenantId, string? currency, CancellationToken cancellationToken)
    {
        // Sólo empresas activas: una dada de baja no cobra. Nada se ordena con la cultura del servidor.
        var active = await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken);
        // Cada empresa activa se ofrece también sin cuenta (spec 2026-11-17 §4.3), con banco, número y moneda en null: esa
        // opción sirve para cualquier moneda, porque la moneda la pone la entrada de la tienda. Va primero dentro de su empresa
        // (el comparador ordena null antes que cualquier texto).
        return active
            .SelectMany(company => company.BankAccounts
                .Select(account => new StoreBillingAccountOption(
                    company.Id.Value, company.Name, account.BankName, account.AccountNumber, account.Currency))
                .Prepend(new StoreBillingAccountOption(company.Id.Value, company.Name, null, null, null)))
            .Where(option => currency is null
                || option.Currency is null
                || string.Equals(option.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(option => option.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.BankName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.AccountNumber, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<StoreBillingAccount> ValidateAsync(
        Guid tenantId,
        Guid companyId,
        string? bankName,
        string? accountNumber,
        string currency,
        CancellationToken cancellationToken)
    {
        VerifiedBillingAccount account;
        try
        {
            // Sin banco ni número el verificador sólo comprueba la empresa y devuelve la moneda que se le pasa: la de la
            // entrada de la tienda (spec 2026-11-17 §4.3).
            account = await QuotationBillingAccountVerifier.VerifyAsync(
                lookup, tenantId, companyId, bankName, accountNumber, currency, cancellationToken);
        }
        catch (QuotationsDomainException exception) when (exception.Code == QuotationBillingAccountVerifier.CompanyNotFoundCode)
        {
            // Contrato A: la empresa que no existe es un 404 (el mismo código para «no existe» y «es de otro tenant»).
            throw new ResourceNotFoundException(exception.Code, exception.Message);
        }

        // Se guarda la cuenta como la tiene la empresa, no como la escribió el cliente HTTP; sin cuenta, banco y número en null.
        return new StoreBillingAccount(
            account.CompanyId, account.BankName, account.AccountNumber, account.Currency.ToUpperInvariant());
    }
}
