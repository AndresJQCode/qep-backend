using Bootstrapper;
using Modules.Ai.Application;
using Modules.Companies.Application;
using Modules.Companies.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan 2026-11-17 A3 (F-2): el editor del agente ofrece las cuentas de cobro con la misma regla que la tienda
/// (<c>ActiveBillingAccountOptions</c>): sólo empresas activas, cada cuenta con su empresa, en orden estable sin cultura.
/// </summary>
public sealed class AiQuotationBillingAccountsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyActiveCompaniesAccountsAreOfferedInStableOrder()
    {
        var verde = NewCompany(
            "verde S.A.S.",
            new CompanyBankAccount { BankName = "Davivienda", AccountNumber = "002", Currency = "COP" },
            new CompanyBankAccount { BankName = "Bancolombia", AccountNumber = "010", Currency = "USD" },
            new CompanyBankAccount { BankName = "Bancolombia", AccountNumber = "009", Currency = "COP" });
        var azul = NewCompany("Azul Ltda.", new CompanyBankAccount { BankName = "Bogotá", AccountNumber = "777", Currency = "COP" });
        var inactive = NewCompany("Antigua S.A.", new CompanyBankAccount { BankName = "Banco", AccountNumber = "1", Currency = "COP" });
        inactive.Deactivate(Now);

        var options = await new AiQuotationBillingAccounts(new SearchableCompanies(verde, azul, inactive)).ListAsync(TenantId, Ct);

        Assert.Equal(
            [
                new AiBillingAccountOption(azul.Id.Value, "Azul Ltda.", "Bogotá", "777", "COP"),
                new AiBillingAccountOption(verde.Id.Value, "verde S.A.S.", "Bancolombia", "009", "COP"),
                new AiBillingAccountOption(verde.Id.Value, "verde S.A.S.", "Bancolombia", "010", "USD"),
                new AiBillingAccountOption(verde.Id.Value, "verde S.A.S.", "Davivienda", "002", "COP"),
            ],
            options);
    }

    private static Company NewCompany(string name, params CompanyBankAccount[] accounts) =>
        Company.Create(
            CompanyId.New(), TenantId, name, accounts,
            "900.111.222-3", Company.ColombiaCountryCode, CompanyDocumentType.Nit, Guid.CreateVersion7(), null,
            CompanyContactInfo.Empty, Now);

    /// <summary>Filtra por estado como el repositorio real: la regla de «sólo activas» la pide la consulta compartida.</summary>
    private sealed class SearchableCompanies(params Company[] companies) : ICompanyRepository
    {
        public Task<IReadOnlyList<Company>> SearchAsync(
            Guid tenantId, string? search, string? name, string? taxId, CompanyStatusFilter? status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Company>>(
            [
                .. companies.Where(company => company.TenantId == tenantId
                    && status switch
                    {
                        CompanyStatusFilter.Active => company.IsActive,
                        CompanyStatusFilter.Inactive => !company.IsActive,
                        _ => true,
                    }),
            ]);

        public Task<Company?> FindAsync(Guid tenantId, CompanyId companyId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> ListCountriesAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AnyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(Company company) => throw new NotSupportedException();

        public void Remove(Company company) => throw new NotSupportedException();
    }
}
