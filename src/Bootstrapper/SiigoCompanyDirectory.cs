using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18 §5.2: la empresa que factura con una conexión de Siigo y sus cuentas de cobro, contra Companies. Las cuentas salen de
/// <c>company.BankAccounts</c>, la misma fuente de la que <c>QuotationBillingAccountResolver</c> toma la foto del pedido, y su clave es
/// <see cref="BankAccountKey.Normalize"/> de banco y número: la misma con la que el armador de la factura buscará el vínculo.
/// </summary>
internal sealed class SiigoCompanyDirectory(ICompanyRepository companies) : ISiigoCompanyDirectory
{
    public async Task<SiigoCompanyRef?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken);
        return company is null ? null : new SiigoCompanyRef(company.Id.Value, company.Name, company.IsActive);
    }

    public async Task<IReadOnlyList<SiigoBillingAccountRef>> ListBillingAccountsAsync(
        Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return [];
        }

        // Una cuenta sin clave (banco o número que se vacían al normalizar) no se puede vincular: no se ofrece. Y dos cuentas con la
        // misma clave son la misma para el vínculo: se ofrece una.
        return
        [
            .. company.BankAccounts
                .Select(account => new SiigoBillingAccountRef(
                    account.BankName, account.AccountNumber, BankAccountKey.Normalize(account.BankName, account.AccountNumber) ?? string.Empty))
                .Where(account => account.Key.Length > 0)
                .DistinctBy(account => account.Key),
        ];
    }
}
