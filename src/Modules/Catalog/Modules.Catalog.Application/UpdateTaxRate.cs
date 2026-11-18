using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Domain.Taxes;
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
    decimal? MinimumBase,
    WithholdingScope? AppliesTo = null) : ICommand<TaxRateDto>;

public static class UpdateTaxRateRequestMapping
{
    /// <summary>
    /// Ausente conserva la base, null la quita y un número la reemplaza. Cualquier otra cosa es un 422
    /// <c>validation.failed</c> con el campo marcado: no se adivina qué quiso decir el cliente. El alcance
    /// (<c>appliesTo</c>) viaja por nombre; uno desconocido es el mismo 422.
    /// </summary>
    public static UpdateTaxRateCommand ToCommand(this UpdateTaxRateRequest request, Guid tenantId, Guid taxRateId)
    {
        var appliesTo = CatalogEnumParsing.ParseOptional<WithholdingScope>(
            request.AppliesTo, nameof(UpdateTaxRateRequest.AppliesTo));

        return request.MinimumBase.ValueKind switch
        {
            JsonValueKind.Undefined => new(tenantId, taxRateId, request.Name, request.Percentage, true, null, appliesTo),
            JsonValueKind.Null => new(tenantId, taxRateId, request.Name, request.Percentage, false, null, appliesTo),
            JsonValueKind.Number when request.MinimumBase.TryGetDecimal(out var value) =>
                new(tenantId, taxRateId, request.Name, request.Percentage, false, value, appliesTo),
            _ => throw new ValidationException(
            [
                new ValidationFailure(nameof(UpdateTaxRateRequest.MinimumBase), "'Minimum Base' must be a number or null."),
            ]),
        };
    }
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
    IValidator<UpdateTaxRateCommand> validator,
    ITaxRateGeographyLookup geography)
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

        // El alcance no se edita, como el tipo (spec 2026-11-18 §4.2): Companies y Customers lo leen por id al
        // validar una selección, y cambiarlo la dejaría repetida o reteniendo dos veces. Para otro alcance se
        // crea otra tasa.
        if (command.AppliesTo is { } appliesTo && appliesTo != taxRate.AppliesTo)
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.applies_to_immutable",
                "The scope of a tax rate cannot change; create another tax rate instead.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(UpdateTaxRateCommand.AppliesTo)] =
                        ["El alcance no se puede cambiar: crea otra tasa y desactiva esta."]
                });
        }

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

        return await taxRate.ToDtoAsync(geography, cancellationToken);
    }
}
