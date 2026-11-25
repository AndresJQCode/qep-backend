using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record DeleteServiceCommand(Guid TenantId, Guid ServiceId, long ExpectedVersion) : ICommand<bool>;

public sealed class DeleteServiceValidator : AbstractValidator<DeleteServiceCommand>
{
    public DeleteServiceValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Eliminar un contrato de servicio (decisión del owner, 2026-11-19): borrado duro de cualquier estado, aun con cobros pagados;
/// el historial se pierde y la pantalla lo advierte. En la misma transacción se van el servicio, todos sus cobros (con sus
/// <c>reminder_steps</c>), sus cambios programados y su historial de precios: el esquema <c>billing</c> no declara FKs entre
/// ellos, así que nada lo hace solo.
/// Los <c>customer_messages</c> son del cliente y del día, no del cobro, y se quedan.
/// </summary>
public sealed class DeleteServiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractRepository services,
    IChargeRepository charges,
    IServicePriceChangeRepository priceChanges,
    IServicePriceScheduleRepository schedules,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    IValidator<DeleteServiceCommand> validator)
    : ICommandHandler<DeleteServiceCommand, bool>
{
    private static readonly string[] NoFields = [];

    public async Task<bool> HandleAsync(DeleteServiceCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // El servicio primero, acotado al tenant: los cobros y el historial se buscan por servicio, sin tenant.
        var service = await services.FindAsync(command.TenantId, new ServiceContractId(command.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);

        await charges.RemoveByServiceAsync(service.Id, cancellationToken);
        await priceChanges.RemoveByServiceAsync(service.Id, cancellationToken);
        await schedules.RemoveByServiceAsync(command.TenantId, service.Id, cancellationToken);
        services.Remove(service);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ServiceDeleted,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            NoFields,
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
