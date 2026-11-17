using BuildingBlocks.Application;
using Modules.Companies.Application;
using Modules.Companies.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.2, plan D5: las empresas del paso «Empresa» del asistente, sobre Companies. Leer va por el
/// repositorio (filtra por tenant); crear corre el caso de uso de Companies entero (autorización con
/// <c>companies.company.manage</c>, validador, ciudad, siembra de tasas), así que sus excepciones salen tal cual y el asistente
/// responde lo mismo que la pantalla de empresas.
/// </summary>
internal sealed class ShopifySetupCompanies(
    ICompanyRepository companies,
    ICommandHandler<CreateCompanyCommand, CompanyDto> createCompany) : IShopifySetupCompanies
{
    public async Task<IReadOnlyList<ShopifySetupCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await companies.SearchAsync(tenantId, search: null, name: null, taxId: null, CompanyStatusFilter.Active, cancellationToken))
            .Where(company => company.IsActive)
            .OrderBy(company => company.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(company => company.Id.Value)
            .Select(ToRef)
            .ToArray();

    public async Task<ShopifySetupCompanyRef?> FindActiveAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await companies.FindAsync(tenantId, new CompanyId(companyId), cancellationToken) is { IsActive: true } company
            ? ToRef(company)
            : null;

    public async Task<ShopifySetupCompanyRef> CreateAsync(ShopifySetupCompanyDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var created = await createCompany.HandleAsync(
            new CreateCompanyCommand(
                draft.TenantId,
                draft.Name,
                draft.BankAccount is { } account
                    ? [new CompanyBankAccountPayload(account.BankName, account.AccountNumber, account.Currency)]
                    : [],
                draft.TaxId,
                draft.Country,
                draft.DocumentType,
                draft.CityId,
                draft.CityName,
                draft.Phone,
                draft.Email,
                draft.Address,
                ChargesVat: draft.ChargesVat),
            cancellationToken);
        return new ShopifySetupCompanyRef(
            created.Id,
            created.Name,
            created.TaxId,
            created.DocumentType,
            created.Country,
            created.ChargesVat,
            created.BankAccounts.Select(bank => new ShopifySetupBankAccount(bank.BankName, bank.AccountNumber, bank.Currency)).ToArray());
    }

    private static ShopifySetupCompanyRef ToRef(Company company) =>
        new(
            company.Id.Value,
            company.Name,
            company.TaxId,
            company.DocumentType.ToWire(),
            company.Country,
            company.ChargesVat,
            company.BankAccounts.Select(bank => new ShopifySetupBankAccount(bank.BankName, bank.AccountNumber, bank.Currency)).ToArray());
}
