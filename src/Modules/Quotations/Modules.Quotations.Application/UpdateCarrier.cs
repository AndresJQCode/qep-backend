using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UpdateCarrierCommand(
    Guid TenantId,
    Guid CarrierId,
    string? Name,
    IReadOnlyList<string?>? ExternalNames,
    bool SupportsCashOnDelivery,
    long ExpectedVersion) : ICommand<CarrierDto>;

public sealed class UpdateCarrierValidator : AbstractValidator<UpdateCarrierCommand>
{
    public UpdateCarrierValidator()
    {
        RuleFor(command => command.Name).Must(CarrierTextRules.IsValidName).WithMessage(CarrierTextRules.NameMessage);
        RuleFor(command => command.ExternalNames)
            .Must(CarrierTextRules.HasValidCount).WithMessage(CarrierTextRules.ExternalNamesMessage);
        RuleForEach(command => command.ExternalNames)
            .Must(CarrierTextRules.IsValidExternalName).WithMessage(CarrierTextRules.ExternalNameMessage);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>Edita nombre, nombres externos y contraentrega (spec §4.1) con <c>If-Match</c> sobre la
/// versión propia de la transportadora, como el layout del Excel.</summary>
public sealed class UpdateCarrierHandler(
    ICarrierRepository carriers,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateCarrierCommand> validator)
    : ICommandHandler<UpdateCarrierCommand, CarrierDto>
{
    public async Task<CarrierDto> HandleAsync(UpdateCarrierCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var carrier = await carriers.FindAsync(command.TenantId, new CarrierId(command.CarrierId), cancellationToken)
            ?? throw CarrierNotFound.For(command.CarrierId);
        if (carrier.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The carrier changed after it was loaded.");
        }

        carrier.Update(
            command.Name ?? string.Empty,
            CarrierTextRules.Clean(command.ExternalNames),
            command.SupportsCashOnDelivery,
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return CarrierMapping.ToDto(carrier);
    }
}
