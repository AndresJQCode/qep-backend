using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record UpdateTaxRateCommand(
    Guid TenantId,
    Guid TaxRateId,
    string Name,
    decimal Percentage,
    bool KeepMinimumBase,
    decimal? MinimumBase) : ICommand<TaxRateDto>;

public static class UpdateTaxRateRequestMapping
{
    /// <summary>
    /// Ausente conserva la base, null la quita y un número la reemplaza. Cualquier otra cosa es un 422
    /// <c>validation.failed</c> con el campo marcado: no se adivina qué quiso decir el cliente.
    /// </summary>
    public static UpdateTaxRateCommand ToCommand(this UpdateTaxRateRequest request, Guid tenantId, Guid taxRateId) =>
        request.MinimumBase.ValueKind switch
        {
            JsonValueKind.Undefined => new(tenantId, taxRateId, request.Name, request.Percentage, true, null),
            JsonValueKind.Null => new(tenantId, taxRateId, request.Name, request.Percentage, false, null),
            JsonValueKind.Number when request.MinimumBase.TryGetDecimal(out var value) =>
                new(tenantId, taxRateId, request.Name, request.Percentage, false, value),
            _ => throw new ValidationException(
            [
                new ValidationFailure(nameof(UpdateTaxRateRequest.MinimumBase), "'Minimum Base' must be a number or null."),
            ]),
        };
}

public sealed class UpdateTaxRateValidator : AbstractValidator<UpdateTaxRateCommand>
{
    public UpdateTaxRateValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(TaxRate.NameMaxLength);
        RuleFor(command => command.Percentage)
            .InclusiveBetween(TaxRate.MinPercentage, (decimal)TaxRate.MaxPercentage);
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
        taxRate.Update(
            command.Name,
            command.Percentage,
            command.KeepMinimumBase ? taxRate.MinimumBase : command.MinimumBase,
            now);
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
