using BuildingBlocks.Application;
using FluentValidation;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record UpdateCompanyCommand(
    Guid TenantId,
    Guid CompanyId,
    string Name,
    string AccountNumber,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address) : ICommand<CompanyDto>, ICompanyWriteCommand;

// Mismas reglas que el POST, por inclusion y no por copia. Ver CompanyWriteRules.
public sealed class UpdateCompanyValidator : AbstractValidator<UpdateCompanyCommand>
{
    public UpdateCompanyValidator() => Include(new CompanyWriteRules());
}

public sealed class UpdateCompanyHandler(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCompanyCommand> validator)
    : ICommandHandler<UpdateCompanyCommand, CompanyDto>
{
    public async Task<CompanyDto> HandleAsync(
        UpdateCompanyCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razon en CreateCompanyHandler.
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CompaniesPermissions.CompanyManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var company = await repository.FindAsync(
            command.TenantId,
            new Domain.CompanyId(command.CompanyId),
            cancellationToken)
            ?? throw CompanyNotFound.For(command.CompanyId);

        var now = clock.UtcNow;

        // Los tres opcionales se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, asi que un campo ausente se limpia.
        company.Update(
            command.Name,
            command.AccountNumber,
            command.TaxId,
            new CompanyContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address
            },
            now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "companies.company.updated",
            company.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return company.ToDto();
    }
}
