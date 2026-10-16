using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Activar o desactivar (spec §4.3, <c>/activate</c> · <c>/deactivate</c>). Una inactiva
/// conserva su nombre (D-F16) y los documentos que ya la tienen la conservan.</summary>
public sealed record SetCarrierActiveCommand(Guid TenantId, Guid CarrierId, bool IsActive) : ICommand<CarrierDto>;

public sealed class SetCarrierActiveHandler(
    ICarrierRepository carriers,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SetCarrierActiveCommand, CarrierDto>
{
    public async Task<CarrierDto> HandleAsync(SetCarrierActiveCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        var carrier = await carriers.FindAsync(command.TenantId, new CarrierId(command.CarrierId), cancellationToken)
            ?? throw CarrierNotFound.For(command.CarrierId);
        // Ya en el estado pedido: no hay cambio que guardar ni versión que subir.
        if (carrier.IsActive == command.IsActive)
        {
            return CarrierMapping.ToDto(carrier);
        }

        if (command.IsActive)
        {
            carrier.Activate(clock.UtcNow);
        }
        else
        {
            carrier.Deactivate(clock.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CarrierMapping.ToDto(carrier);
    }
}
