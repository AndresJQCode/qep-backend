using BuildingBlocks.Application;
using BuildingBlocks.Domain.Taxes;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <param name="CountryExplicit">El cuerpo trajo <c>country</c>. Sólo entonces se verifica que el tenant
/// tenga una empresa en ese país; el CO implícito de un cuerpo viejo no se verifica (spec §9.2).</param>
public sealed record CreateTaxRateCommand(
    Guid TenantId,
    string Name,
    decimal Percentage,
    string Type,
    string Country,
    bool CountryExplicit,
    decimal? MinimumBase)
    : ICommand<TaxRateDto>;

// El dominio hace cumplir las mismas reglas y tiraría un 422 con un solo código. El validador
// existe para que la respuesta lleve el mapa de errores por campo que ApiExceptionHandler arma
// desde ValidationException, que es lo que un formulario necesita para marcar el input culpable.
// Las reglas de tipo, fracción y base mínima no están acá: las da el dominio (TaxCountries).
public sealed class CreateTaxRateValidator : AbstractValidator<CreateTaxRateCommand>
{
    public CreateTaxRateValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(TaxRate.NameMaxLength);
        RuleFor(command => command.Percentage)
            .InclusiveBetween(TaxRate.MinPercentage, (decimal)TaxRate.MaxPercentage);
        RuleFor(command => command.Type)
            .MaximumLength(TaxRate.TypeMaxLength);
        RuleFor(command => command.Country)
            .Must(country => country?.Trim().Length == TaxRate.CountryLength)
            .WithMessage("'Country' must be a two-letter country code.");
    }
}

public sealed class CreateTaxRateHandler(
    ITaxRateRepository repository,
    ITaxRateCountrySource countrySource,
    ITaxUnitValueRepository taxUnitValueRepository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateTaxRateCommand> validator)
    : ICommandHandler<CreateTaxRateCommand, TaxRateDto>
{
    public async Task<TaxRateDto> HandleAsync(
        CreateTaxRateCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar, y no al revés. La política del endpoint ya frena a quien le
        // falta el permiso, pero no al que lo tiene para otro tenant: a ése lo rechaza esta
        // revalidación. Validando primero, ese llamador ajeno se lleva el mapa de errores por
        // campo —la forma del contrato— antes de que nadie le diga que no. Lo encontró la
        // revisión de riesgo de CAT-02.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.TaxRateManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        if (command.CountryExplicit)
        {
            await EnsureCountryAvailableAsync(command, cancellationToken);
        }

        var now = clock.UtcNow;
        var taxRate = TaxRate.Create(
            TaxRateId.New(),
            command.TenantId,
            command.Name,
            command.Percentage,
            command.Type,
            command.Country,
            command.MinimumBase,
            now);

        // Después del dominio, para que un tipo que no admite base diga minimum_base_not_allowed
        // antes que tax_unit_value_missing. Una base en unidades sin ningún valor cargado no se
        // puede convertir a moneda, y la tasa quedaría guardada sin poder aplicarse nunca.
        if (taxRate.MinimumBase is not null
            && TaxCountries.Find(taxRate.Country)?.TaxUnitName is not null
            && !await taxUnitValueRepository.AnyAsync(taxRate.Country, cancellationToken))
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.tax_unit_value_missing",
                $"No tax unit value is loaded for country '{taxRate.Country}'.");
        }

        repository.Add(taxRate);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.tax_rate.created",
            taxRate.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return taxRate.ToDto();
    }

    private async Task EnsureCountryAvailableAsync(
        CreateTaxRateCommand command,
        CancellationToken cancellationToken)
    {
        // Se compara el código canónico: Find recorta y pasa a mayúsculas, y las empresas guardan el suyo así.
        var definition = TaxCountries.Find(command.Country);
        var companyCountries = definition is null
            ? null
            : await countrySource.ListCompanyCountriesAsync(command.TenantId, cancellationToken);

        if (definition is null || !companyCountries!.Contains(definition.Country))
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.country_not_available",
                $"The tenant has no company in country '{command.Country.Trim().ToUpperInvariant()}' with tax types.");
        }
    }
}
