using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary><c>ExternalNames</c> nullable: un cuerpo sin la lista es una transportadora sin nombres
/// externos, que es lo normal hasta que se conecta una integración.</summary>
public sealed record CreateCarrierCommand(
    Guid TenantId,
    string? Name,
    IReadOnlyList<string?>? ExternalNames,
    bool SupportsCashOnDelivery) : ICommand<CarrierDto>;

/// <summary>Las reglas de texto libre de crear y editar, una sola vez: el dominio da el código, el
/// validador da el campo (<c>Name</c>, <c>ExternalNames[i]</c>), que es lo que el formulario marca.</summary>
internal static class CarrierTextRules
{
    public static readonly string NameMessage =
        $"'Name' is required and cannot exceed {CarrierName.MaxLength} characters.";

    public static readonly string ExternalNamesMessage =
        $"'External Names' cannot have more than {CarrierName.MaxExternalNames} items.";

    public static readonly string ExternalNameMessage =
        $"Each external name is required and cannot exceed {CarrierName.ExternalMaxLength} characters.";

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= CarrierName.MaxLength;

    public static bool HasValidCount(IReadOnlyList<string?>? externalNames) =>
        externalNames is null || externalNames.Count <= CarrierName.MaxExternalNames;

    public static bool IsValidExternalName(string? externalName) =>
        !string.IsNullOrWhiteSpace(externalName) && externalName.Trim().Length <= CarrierName.ExternalMaxLength;

    public static IReadOnlyCollection<string> Clean(IReadOnlyList<string?>? externalNames) =>
        [.. (externalNames ?? []).Select(value => value ?? string.Empty)];
}

public sealed class CreateCarrierValidator : AbstractValidator<CreateCarrierCommand>
{
    public CreateCarrierValidator()
    {
        RuleFor(command => command.Name).Must(CarrierTextRules.IsValidName).WithMessage(CarrierTextRules.NameMessage);
        RuleFor(command => command.ExternalNames)
            .Must(CarrierTextRules.HasValidCount).WithMessage(CarrierTextRules.ExternalNamesMessage);
        RuleForEach(command => command.ExternalNames)
            .Must(CarrierTextRules.IsValidExternalName).WithMessage(CarrierTextRules.ExternalNameMessage);
    }
}

/// <summary>Nace activa y al final del orden (spec §4.3). La unicidad de los nombres la cierra el
/// índice <c>IX_carrier_names_tenant_name</c>, que <c>QuotationsUnitOfWork</c> traduce a
/// <c>quotations.carrier.name_taken</c>: una comprobación previa acá no cerraría la carrera.</summary>
public sealed class CreateCarrierHandler(
    ICarrierRepository carriers,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CreateCarrierCommand> validator)
    : ICommandHandler<CreateCarrierCommand, CarrierDto>
{
    public const string AuditAction = "quotations.carrier.created";

    public async Task<CarrierDto> HandleAsync(CreateCarrierCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: a otro tenant no se le devuelve el mapa de campos.
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var existing = await carriers.ListAsync(command.TenantId, cancellationToken);
        var sortOrder = existing.Count == 0 ? 0 : existing.Max(carrier => carrier.SortOrder) + 1;
        var now = clock.UtcNow;
        var carrier = Carrier.Create(
            CarrierId.New(),
            command.TenantId,
            command.Name ?? string.Empty,
            CarrierTextRules.Clean(command.ExternalNames),
            command.SupportsCashOnDelivery,
            sortOrder,
            now);
        carriers.Add(carrier);
        // Por outbox, en la misma transacción que la transportadora (como el resto de la configuración).
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, AuditAction, carrier.Id.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return CarrierMapping.ToDto(carrier);
    }
}
