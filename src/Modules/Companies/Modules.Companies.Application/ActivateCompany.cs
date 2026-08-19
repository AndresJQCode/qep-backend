using BuildingBlocks.Application;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record ActivateCompanyCommand(Guid TenantId, Guid CompanyId)
    : ICommand<CompanyDto>;

// Sin validador, por la misma razon que DeactivateCompany: el comando no lleva texto libre.
// Activar algo ya activo lo rechaza el agregado. Sin permiso nuevo — activar es administrar.
public sealed class ActivateCompanyHandler(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ActivateCompanyCommand, CompanyDto>
{
    public async Task<CompanyDto> HandleAsync(
        ActivateCompanyCommand command,
        CancellationToken cancellationToken)
    {
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CompaniesPermissions.CompanyManage);

        var company = await repository.FindAsync(
            command.TenantId, new CompanyId(command.CompanyId), cancellationToken)
            ?? throw CompanyNotFound.For(command.CompanyId);

        var now = clock.UtcNow;
        company.Activate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "companies.company.activated",
            company.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return company.ToDto();
    }
}
