using BuildingBlocks.Application;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record ListCompaniesQuery(
    Guid TenantId,
    string? Search,
    CompanyStatusFilter? Status) : IQuery<IReadOnlyList<CompanyDto>>;

public sealed class ListCompaniesHandler(
    ICompanyRepository repository,
    ICompanyGeographyLookup geographyLookup,
    IExecutionContext executionContext)
    : IQueryHandler<ListCompaniesQuery, IReadOnlyList<CompanyDto>>
{
    public async Task<IReadOnlyList<CompanyDto>> HandleAsync(
        ListCompaniesQuery query,
        CancellationToken cancellationToken)
    {
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CompaniesPermissions.CompanyRead);

        var companies = await repository.SearchAsync(
            query.TenantId, query.Search, query.Status, cancellationToken);

        // Una sola consulta en lote para toda la pagina, no una por empresa — mismo criterio
        // que ListCustomersHandler con FindCitiesAsync.
        var cityIds = companies.Select(company => company.CityId).Distinct().ToArray();
        var citiesById = await geographyLookup.FindCitiesAsync(cityIds, cancellationToken);

        return companies
            .Select(company => company.ToDto(ResolveCity(citiesById, company)))
            .ToArray();
    }

    // La FK de base garantiza que la ciudad exista: un miss aca es corrupcion de datos, no una
    // entrada de usuario invalida. Mismo criterio que CompanyMapping.ToDtoAsync.
    private static CompanyCityRef ResolveCity(
        IReadOnlyDictionary<Guid, CompanyCityRef> citiesById, Company company) =>
        citiesById.TryGetValue(company.CityId, out var city)
            ? city
            : throw new InvalidOperationException(
                $"City '{company.CityId}' referenced by company '{company.Id}' was not found.");
}
