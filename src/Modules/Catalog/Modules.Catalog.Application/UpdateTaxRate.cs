using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record UpdateTaxRateCommand(
    Guid TenantId,
    Guid TaxRateId,
    string Name,
    int Percentage) : ICommand<TaxRateDto>;

public sealed class UpdateTaxRateValidator : AbstractValidator<UpdateTaxRateCommand>
{
    public UpdateTaxRateValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(TaxRate.NameMaxLength);
        RuleFor(command => command.Percentage)
            .InclusiveBetween(TaxRate.MinPercentage, TaxRate.MaxPercentage);
    }
}

public sealed class UpdateTaxRateHandler(
    ITaxRateRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateTaxRateCommand> validator)
    : ICommandHandler<UpdateTaxRateCommand, TaxRateDto>
{
    public async Task<TaxRateDto> HandleAsync(
        UpdateTaxRateCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razón en CreateTaxRateHandler.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.TaxRateManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var taxRate = await repository.FindAsync(
            command.TenantId, new TaxRateId(command.TaxRateId), cancellationToken)
            ?? throw TaxRateNotFound.For(command.TaxRateId);

        var now = clock.UtcNow;
        taxRate.Update(command.Name, command.Percentage, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.tax_rate.updated",
            taxRate.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return taxRate.ToDto();
    }
}
