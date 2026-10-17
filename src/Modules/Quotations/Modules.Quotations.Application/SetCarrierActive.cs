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
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SetCarrierActiveCommand, CarrierDto>
{
    public const string ActivatedAuditAction = "quotations.carrier.activated";

    public const string DeactivatedAuditAction = "quotations.carrier.deactivated";

    public async Task<CarrierDto> HandleAsync(SetCarrierActiveCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        var carrier = await carriers.FindAsync(command.TenantId, new CarrierId(command.CarrierId), cancellationToken)
            ?? throw CarrierNotFound.For(command.CarrierId);
        // Ya en el estado pedido: no hay cambio que guardar, ni versión que subir, ni nada que auditar.
        if (carrier.IsActive == command.IsActive)
        {
            return CarrierMapping.ToDto(carrier);
        }

        var now = clock.UtcNow;
        if (command.IsActive)
        {
            carrier.Activate(now);
        }
        else
        {
            carrier.Deactivate(now);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            command.IsActive ? ActivatedAuditAction : DeactivatedAuditAction,
            carrier.Id.ToString(),
            "success",
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CarrierMapping.ToDto(carrier);
    }
}
