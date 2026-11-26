using Modules.Ai.Application;
using Modules.Companies.Application;

namespace Bootstrapper;

/// <summary>
/// Companies → Ai: las cuentas de cobro que el editor del agente ofrece para los borradores de cotización (plan 2026-11-17 A3). La regla
/// es la de la tienda (<see cref="ActiveBillingAccountOptions"/>), no una copia: si cambia qué cuenta se puede ofrecer, cambian las dos.
/// </summary>
internal sealed class AiQuotationBillingAccounts(ICompanyRepository companies) : IAiQuotationBillingAccounts
{
    public async Task<IReadOnlyList<AiBillingAccountOption>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var options = await ActiveBillingAccountOptions.ListAsync(companies, tenantId, currency: null, cancellationToken);
        return
        [
            // Sin includeWithoutAccount toda opción trae banco, número y moneda.
            .. options.Select(option => new AiBillingAccountOption(
                option.CompanyId, option.CompanyName, option.BankName!, option.AccountNumber!, option.Currency!)),
        ];
    }
}
