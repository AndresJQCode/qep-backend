using BuildingBlocks.Application;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record GetCompanyQuery(Guid TenantId, Guid CompanyId) : IQuery<CompanyDto>;

public sealed class GetCompanyHandler(
    ICompanyRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetCompanyQuery, CompanyDto>
{
    public async Task<CompanyDto> HandleAsync(
        GetCompanyQuery query,
        CancellationToken cancellationToken)
    {
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CompaniesPermissions.CompanyRead);

        var company = await repository.FindAsync(
            query.TenantId, new CompanyId(query.CompanyId), cancellationToken)
            ?? throw CompanyNotFound.For(query.CompanyId);

        return company.ToDto();
    }
}
