using BuildingBlocks.Application;
using FluentValidation;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record CreateCompanyCommand(
    Guid TenantId,
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string? Phone,
    string? Email,
    string? Address) : ICommand<CompanyDto>, ICompanyWriteCommand;

// Las reglas viven en CompanyWriteRules y se incluyen, no se copian. Ver el hallazgo `D` alla.
public sealed class CreateCompanyValidator : AbstractValidator<CreateCompanyCommand>
{
    public CreateCompanyValidator() => Include(new CompanyWriteRules());
}

public sealed class CreateCompanyHandler(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCompanyCommand> validator)
    : ICommandHandler<CreateCompanyCommand, CompanyDto>
{
    public async Task<CompanyDto> HandleAsync(
        CreateCompanyCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, y no al reves. La politica del endpoint ya frena a quien le
        // falta el permiso, pero no al que lo tiene para otro tenant: a ese lo rechaza esta
        // revalidacion. Validando primero, ese llamador ajeno se lleva el mapa de errores por
        // campo —la forma del contrato— antes de que nadie le diga que no. Lo encontro la
        // revision de riesgo de CAT-02.
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CompaniesPermissions.CompanyManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var company = Company.Create(
            CompanyId.New(),
            command.TenantId,
            command.Name,
            command.BankAccounts.ToDomain(),
            command.TaxId,
            new CompanyContactInfo
            {
                Phone = command.Phone,
                Email = command.Email,
                Address = command.Address
            },
            now);

        repository.Add(company);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "companies.company.created",
            company.Id.ToString(),
            "success",
            now);

        // Desde EMP-08 no queda ninguna unicidad que arbitre la base para este agregado: la
        // regla que sobrevive —que una empresa no repita la misma cuenta— es invariante del
        // agregado y ya la hizo cumplir Company.Create en memoria. Por eso este SaveChanges no
        // tiene detras ninguna rama de traduccion de 23505.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return company.ToDto();
    }
}
