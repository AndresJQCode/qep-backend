using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record ListCompaniesQuery(
    Guid TenantId,
    string? Search,
    CompanyStatusFilter? Status) : IQuery<IReadOnlyList<CompanyDto>>;

public sealed class ListCompaniesHandler(
    ICompanyRepository repository,
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

        return companies.Select(company => company.ToDto()).ToArray();
    }
}
