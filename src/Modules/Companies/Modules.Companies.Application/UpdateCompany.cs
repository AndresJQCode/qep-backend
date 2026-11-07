using BuildingBlocks.Application;
using FluentValidation;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record UpdateCompanyCommand(
    Guid TenantId,
    Guid CompanyId,
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string? Country,
    string? DocumentType,
    Guid? CityId,
    string? CityName,
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
    ICompanyGeographyLookup geographyLookup,
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

        // Cargar la empresa va antes de validar (y no despues, como antes): el cliente viejo no
        // manda pais, y el pais efectivo es el de la empresa guardada. Sin ella no se sabe que
        // carril validar. Autorizar sigue primero, asi un llamador ajeno no ve ni un 404.
        var company = await repository.FindAsync(
            command.TenantId,
            new Domain.CompanyId(command.CompanyId),
            cancellationToken)
            ?? throw CompanyNotFound.For(command.CompanyId);

        command = CompanyLegacyWrite.Resolve(command, company);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Fuera de Colombia la ciudad es texto libre: no hay nada que buscar en Geography.
        CompanyCityRef? city = null;
        if (command.Country == Company.ColombiaCountryCode)
        {
            city = await geographyLookup.FindCityAsync(command.CityId!.Value, cancellationToken)
                ?? throw new CompaniesDomainException(
                    "companies.company.city_not_found",
                    "The city was not found.");
        }

        var now = clock.UtcNow;

        // Los tres opcionales se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, asi que un campo ausente se limpia. La coleccion de cuentas sigue la misma
        // regla — la lista que llega es la lista que queda, y quitar una cuenta es mandar el PUT
        // sin ella.
        company.Update(
            command.Name,
            command.BankAccounts.ToDomain(),
            command.TaxId,
            command.Country!,
            CompanyDocumentTypes.Parse(command.DocumentType!),
            command.CityId,
            command.CityName,
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

        return company.ToDto(city);
    }
}
