using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
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
    string? Address,
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    bool? ChargesVat = null,
    // Spec 2026-11-23: la región (provincia) del domicilio de un país con catálogo distinto de Colombia; null es «sin región».
    Guid? RegionId = null) : ICommand<CompanyDto>, ICompanyWriteCommand;

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
    IValidator<UpdateCompanyCommand> validator,
    ICompanyTaxRateLookup taxRateLookup,
    ICompanyDefaultTaxRates defaultTaxRates,
    ILogger<UpdateCompanyHandler> logger)
    : ICommandHandler<UpdateCompanyCommand, CompanyDto>
{
    public async Task<CompanyDto> HandleAsync(
        UpdateCompanyCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razon en CreateCompanyHandler.
        CompaniesAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CompaniesPermissions.CompanyManage);

        // El pais se normaliza una sola vez, antes de validar. Ver la razon en CreateCompanyHandler.
        // Validar va antes de cargar la empresa: el pais y el tipo llegan siempre en el cuerpo, asi
        // que la empresa guardada no hace falta para saber que carril de ciudad validar.
        command = command with { Country = CompanyWriteRules.NormalizeCountry(command.Country) };
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var company = await repository.FindAsync(
            command.TenantId,
            new Domain.CompanyId(command.CompanyId),
            cancellationToken)
            ?? throw CompanyNotFound.For(command.CompanyId);

        // Fuera de Colombia la ciudad es texto libre: no hay nada que buscar en Geography.
        CompanyCityRef? city = null;
        if (command.Country == Company.ColombiaCountryCode)
        {
            city = await geographyLookup.FindCityAsync(command.CityId!.Value, cancellationToken)
                ?? throw new CompaniesDomainException(
                    "companies.company.city_not_found",
                    "The city was not found.");
        }

        // Spec 2026-11-23: la región existe y es del país de la empresa. Antes de tocar el agregado, como la ciudad.
        var region = await CompanyRegionRules.ResolveRegionAsync(
            geographyLookup, command.Country!, command.RegionId, cancellationToken);

        // Spec ReteICA por empresa §7.3: ausente o null conserva las guardadas, pero la lista resultante se valida igual
        // contra el país del cuerpo, que puede haber cambiado. Lo ya guardado e inactivo se tolera (A16).
        var withholdingTaxRateIds = command.WithholdingTaxRateIds ?? company.WithholdingTaxRateIds;
        var withholdingTaxRates = await taxRateLookup.FindManyAsync(
            command.TenantId, withholdingTaxRateIds, cancellationToken);
        CompanyWithholdingRules.Validate(
            withholdingTaxRates, withholdingTaxRateIds, company.WithholdingTaxRateIds, command.Country!);

        var now = clock.UtcNow;
        // Spec 2026-11-17 §5.3: la siembra corre sólo si la empresa *pasa* a ser colombiana con IVA (prendió el IVA o se
        // mudó a CO), no en cada edición de una que ya lo era.
        var neededDefaultRatesBefore = CompanyDefaultTaxRateSeeding.Applies(company.ChargesVat, company.Country);

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
            now,
            // Ausente conserva (no «true»): un frontend que todavía no manda el campo no puede
            // prender el IVA que otro apagó. Mismo criterio que las tasas de retención.
            command.ChargesVat ?? company.ChargesVat,
            // El PUT reemplaza: sin región en el cuerpo, la guardada se borra (igual que CityName).
            command.RegionId);
        company.SetWithholdingTaxRates(withholdingTaxRateIds, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "companies.company.updated",
            company.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (!neededDefaultRatesBefore && CompanyDefaultTaxRateSeeding.Applies(company.ChargesVat, company.Country))
        {
            await CompanyDefaultTaxRateSeeding.TryEnsureAsync(
                defaultTaxRates, logger, command.TenantId, executionContext.SubjectId, cancellationToken);
        }

        return company.ToDto(
            city,
            await CompanyMapping.ResolveWithholdingTaxRatesAsync(
                company.WithholdingTaxRateIds, withholdingTaxRates, geographyLookup, cancellationToken),
            region);
    }
}
