using Modules.Companies.Application;

namespace Bootstrapper;

/// <summary>Una cuenta de cobro que se puede ofrecer, con su empresa.</summary>
/// <remarks>Banco, número y moneda van en <c>null</c> en la opción «sin cuenta» de una empresa (sólo la tienda la pide).</remarks>
internal sealed record ActiveBillingAccountOption(Guid CompanyId, string CompanyName, string? BankName, string? AccountNumber, string? Currency);

/// <summary>
/// Las cuentas de cobro que una pantalla ofrece: las de las empresas activas, en orden por empresa, banco y número, sin la cultura del
/// servidor. La usan la tienda (<see cref="StoreBillingAccounts"/>) y el editor del agente (<see cref="AiQuotationBillingAccounts"/>), así
/// que las dos listas salen de una sola regla (plan 2026-11-17 A3). Es la misma elegibilidad que el editor de cotizaciones del frontend
/// (sólo activas); <c>QuotationBillingAccountVerifier</c> comprueba la cuenta al crear.
/// </summary>
internal static class ActiveBillingAccountOptions
{
    /// <param name="currency"><c>null</c> lista todas; si no, sólo las de esa moneda, sin mayúsculas.</param>
    /// <param name="includeWithoutAccount">
    /// Si es <c>true</c>, cada empresa activa se ofrece también sin cuenta (spec 2026-11-17 §4.3), con banco, número y moneda en
    /// <c>null</c>: esa opción sirve para cualquier moneda, porque la moneda la pone quien la elige (la entrada de la tienda). Va
    /// primero dentro de su empresa (el comparador ordena <c>null</c> antes que cualquier texto). El editor del agente no la pide:
    /// su cuenta de cobro exige banco (<c>AgentBillingAccount</c>).
    /// </param>
    public static async Task<IReadOnlyList<ActiveBillingAccountOption>> ListAsync(
        ICompanyRepository companies, Guid tenantId, string? currency, CancellationToken cancellationToken,
        bool includeWithoutAccount = false)
    {
        ArgumentNullException.ThrowIfNull(companies);
        // Sólo empresas activas: una dada de baja no cobra. Nada se ordena con la cultura del servidor.
        var active = await companies.SearchAsync(tenantId, null, null, null, CompanyStatusFilter.Active, cancellationToken);
        return active
            .SelectMany(company =>
            {
                var accounts = company.BankAccounts.Select(account => new ActiveBillingAccountOption(
                    company.Id.Value, company.Name, account.BankName, account.AccountNumber, account.Currency));
                return includeWithoutAccount
                    ? accounts.Prepend(new ActiveBillingAccountOption(company.Id.Value, company.Name, null, null, null))
                    : accounts;
            })
            .Where(option => currency is null
                || option.Currency is null
                || string.Equals(option.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(option => option.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.BankName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.AccountNumber, StringComparer.Ordinal)
            .ToList();
    }
}
