using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Companies.Domain;
using Modules.Tenancy.Application;

namespace Modules.Companies.Application;

public sealed record CreateCompanyCommand(
    Guid TenantId,
    string Name,
    IReadOnlyList<CompanyBankAccountPayload> BankAccounts,
    string TaxId,
    string? Country,
    string? DocumentType,
    Guid? CityId,
    string? CityName,
    string? Phone,
    string? Email,
    string? Address,
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    bool? ChargesVat = null,
    // Spec 2026-11-23: la región (provincia) del domicilio de un país con catálogo distinto de Colombia; null es «sin región».
    Guid? RegionId = null) : ICommand<CompanyDto>, ICompanyWriteCommand;

// Las reglas viven en CompanyWriteRules y se incluyen, no se copian. Ver el hallazgo `D` alla.
public sealed class CreateCompanyValidator : AbstractValidator<CreateCompanyCommand>
{
    public CreateCompanyValidator() => Include(new CompanyWriteRules());
}

public sealed class CreateCompanyHandler(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    ICompanyGeographyLookup geographyLookup,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCompanyCommand> validator,
    ICompanyTaxRateLookup taxRateLookup,
    ICompanyDefaultTaxRates defaultTaxRates,
    ILogger<CreateCompanyHandler> logger)
    : ICommandHandler<CreateCompanyCommand, CompanyDto>
{
    private readonly CompanyCreation creation = new(
        repository, unitOfWork, auditPublisher, geographyLookup, clock, validator, taxRateLookup, defaultTaxRates, logger);

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

        // Validar, resolver ciudad y retenciones, crear, auditar y guardar viven en CompanyCreation:
        // las comparte con el alta del sistema (ICompanyProvisioning). Aquí se queda lo que es de este
        // endpoint: autorizar, el actor de la sesión y la forma de la respuesta.
        var created = await creation.CreateAsync(command, executionContext.SubjectId, cancellationToken);

        // Spec 2026-11-17 §5.3: después del commit, nunca dentro. Un fallo no deshace la empresa.
        await creation.SeedDefaultsAsync(created.Company, executionContext.SubjectId, cancellationToken);

        return created.Company.ToDto(
            created.City,
            await CompanyMapping.ResolveWithholdingTaxRatesAsync(
                created.Company.WithholdingTaxRateIds, created.WithholdingRates, geographyLookup, cancellationToken),
            created.Region);
    }
}
