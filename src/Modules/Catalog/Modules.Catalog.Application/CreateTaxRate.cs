using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record CreateTaxRateCommand(Guid TenantId, string Name, int Percentage)
    : ICommand<TaxRateDto>;

// El dominio hace cumplir las mismas reglas y tiraría un 422 con un solo código. El validador
// existe para que la respuesta lleve el mapa de errores por campo que ApiExceptionHandler arma
// desde ValidationException, que es lo que un formulario necesita para marcar el input culpable.
public sealed class CreateTaxRateValidator : AbstractValidator<CreateTaxRateCommand>
{
    public CreateTaxRateValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(TaxRate.NameMaxLength);
        RuleFor(command => command.Percentage)
            .InclusiveBetween(TaxRate.MinPercentage, TaxRate.MaxPercentage);
    }
}

public sealed class CreateTaxRateHandler(
    ITaxRateRepository repository,
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

        var now = clock.UtcNow;
        var taxRate = TaxRate.Create(
            TaxRateId.New(), command.TenantId, command.Name, command.Percentage, now);

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
}
