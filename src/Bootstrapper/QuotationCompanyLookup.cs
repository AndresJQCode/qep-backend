using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta el módulo de empresas al puerto que <c>quotations</c> declara para saber con qué cuenta
/// se factura. Vive acá, como el resto de los adaptadores entre módulos.
/// </summary>
internal sealed class QuotationCompanyLookup(ICompanyRepository repository)
    : IQuotationCompanyLookup
{
    public async Task<QuotationCompanyRef?> FindAsync(
        Guid tenantId,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var company = await repository.FindAsync(
            tenantId, new CompanyId(companyId), cancellationToken);

        return company is null
            ? null
            : new QuotationCompanyRef(
                company.Id.Value,
                company.Name,
                company.TaxId,
                company.IsActive,
                company.BankAccounts
                    .Select(account => new QuotationCompanyAccountRef(
                        account.BankName, account.AccountNumber, account.Currency))
                    .ToArray());
    }
}
