using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>
/// El alta de una empresa, sin autorización ni contexto de ejecución, para que la compartan el handler de
/// <c>POST /companies</c> (que autoriza y manda al actor de la sesión) y <see cref="CompanyProvisioning"/> (el alta del
/// sistema al aceptar una invitación de mayorista, que manda al usuario que acepta). Dos métodos y no uno porque el commit
/// cae en el medio: la siembra de tasas va después de confirmar, nunca dentro.
/// </summary>
internal sealed class CompanyCreation(
    ICompanyRepository repository,
    ICompaniesUnitOfWork unitOfWork,
    ICompaniesAuditPublisher auditPublisher,
    ICompanyGeographyLookup geographyLookup,
    IClock clock,
    IValidator<CreateCompanyCommand> validator,
    ICompanyTaxRateLookup taxRateLookup,
    ICompanyDefaultTaxRates defaultTaxRates,
    ILogger logger)
{
    /// <summary>
    /// Normaliza el país, valida, resuelve la ciudad y las retenciones, crea el agregado, lo audita con
    /// <paramref name="actorId"/> y guarda. No confirma: si hay una transacción abierta, el commit es del llamador.
    /// </summary>
    public async Task<CreatedCompany> CreateAsync(
        CreateCompanyCommand command,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        // El pais se normaliza una sola vez, antes de validar: la busqueda de ciudad de abajo
        // compara contra "CO", y un "co" sin normalizar se la saltaria.
        command = command with { Country = CompanyWriteRules.NormalizeCountry(command.Country) };
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Se resuelve antes de crear el agregado, no despues: un CityId sintacticamente valido
        // pero de una ciudad inexistente tiene que fallar como 422 companies.company.city_not_found
        // en vez de reventar la FK de base como 500 al hacer SaveChanges.
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

        // Spec ReteICA por empresa §7.3: en el alta, ausente es «ninguna». Se valida antes de crear, como la ciudad.
        var withholdingTaxRateIds = command.WithholdingTaxRateIds ?? [];
        var withholdingTaxRates = await taxRateLookup.FindManyAsync(
            command.TenantId, withholdingTaxRateIds, cancellationToken);
        CompanyWithholdingRules.Validate(withholdingTaxRates, withholdingTaxRateIds, [], command.Country!);

        var now = clock.UtcNow;
        var company = Company.Create(
            CompanyId.New(),
            command.TenantId,
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
            // Spec 2026-11-17 §5.1: ausente es «cobra IVA».
            command.ChargesVat ?? true,
            command.RegionId);
        company.SetWithholdingTaxRates(withholdingTaxRateIds, now);

        repository.Add(company);
        auditPublisher.Publish(
            command.TenantId,
            actorId,
            "companies.company.created",
            company.Id.ToString(),
            "success",
            now);

        // Desde EMP-08 no queda ninguna unicidad que arbitre la base para este agregado: la
        // regla que sobrevive —que una empresa no repita la misma cuenta— es invariante del
        // agregado y ya la hizo cumplir Company.Create en memoria. Por eso este SaveChanges no
        // tiene detras ninguna rama de traduccion de 23505.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreatedCompany(company, city, withholdingTaxRates, region);
    }

    /// <summary>
    /// Spec 2026-11-17 §5.3: después del commit, nunca dentro. Un fallo no deshace la empresa.
    /// </summary>
    public async Task SeedDefaultsAsync(Company company, Guid actorId, CancellationToken cancellationToken)
    {
        if (CompanyDefaultTaxRateSeeding.Applies(company.ChargesVat, company.Country))
        {
            await CompanyDefaultTaxRateSeeding.TryEnsureAsync(
                defaultTaxRates, logger, company.TenantId, actorId, cancellationToken);
        }
    }
}

/// <summary>La empresa recién guardada y lo que el handler necesita para armar su DTO sin volver a consultar.</summary>
internal sealed record CreatedCompany(
    Company Company,
    CompanyCityRef? City,
    IReadOnlyDictionary<Guid, CompanyTaxRateRef> WithholdingRates,
    CompanyRegionRef? Region);
