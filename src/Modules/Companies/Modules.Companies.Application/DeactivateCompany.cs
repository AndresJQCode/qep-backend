using BuildingBlocks.Application;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record DeactivateCompanyCommand(Guid TenantId, Guid CompanyId)
    : ICommand<CompanyDto>;

// Sin validador: el comando no lleva texto libre. Desactivar dos veces lo rechaza el agregado,
// que es donde va esa regla.
public sealed class DeactivateCompanyHandler(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    ICompanyGeographyLookup geographyLookup,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeactivateCompanyCommand, CompanyDto>
{
    public async Task<CompanyDto> HandleAsync(
        DeactivateCompanyCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio, no despues: consultar primero le confirma al
        // llamador que el id existe. La revision de CAT-02 ya corrigio ese orden una vez.
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CompaniesPermissions.CompanyManage);

        var company = await repository.FindAsync(
            command.TenantId, new CompanyId(command.CompanyId), cancellationToken)
            ?? throw CompanyNotFound.For(command.CompanyId);

        var now = clock.UtcNow;
        company.Deactivate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "companies.company.deactivated",
            company.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await company.ToDtoAsync(geographyLookup, cancellationToken);
    }
}
