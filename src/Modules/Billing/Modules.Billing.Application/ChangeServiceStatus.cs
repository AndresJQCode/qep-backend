using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

public enum BillingServiceTransition
{
    Pause = 1,
    Resume = 2,
    Cancel = 3,
}

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ChangeServiceStatusCommand(
    Guid TenantId,
    Guid ServiceId,
    BillingServiceTransition Transition,
    long ExpectedVersion)
    : ICommand<ServiceDetailDto>;

public sealed class ChangeServiceStatusValidator : AbstractValidator<ChangeServiceStatusCommand>
{
    public ChangeServiceStatusValidator()
    {
        RuleFor(command => command.Transition).IsInEnum();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Pausar, reanudar y cancelar (spec §3.2). Pausar no toca los cobros: los <c>Pending</c> siguen pendientes y avisándose.
/// Cancelar pasa <b>todos</b> los <c>Pending</c> a <c>Cancelled</c> en la misma transacción. Reanudar sólo escribe la fecha de
/// reactivación: los períodos en pausa no se cobran y el worker retoma desde la primera ancla de ahí en adelante.
/// </summary>
public sealed class ChangeServiceStatusHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IBillingServiceRepository services,
    IChargeRepository charges,
    IBillingUnitOfWork unitOfWork,
    IBillingAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<ChangeServiceStatusCommand> validator)
    : ICommandHandler<ChangeServiceStatusCommand, ServiceDetailDto>
{
    private static readonly string[] StatusField = ["status"];

    public async Task<ServiceDetailDto> HandleAsync(ChangeServiceStatusCommand command, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, BillingPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Primero el servicio, acotado al tenant: ListPendingByServiceAsync no lleva tenant y cancelaría cobros ajenos.
        var service = await services.FindAsync(command.TenantId, new BillingServiceId(command.ServiceId), cancellationToken)
            ?? throw BillingNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);

        var now = clock.UtcNow;
        string action;
        switch (command.Transition)
        {
            case BillingServiceTransition.Pause:
                service.Pause(now);
                action = BillingAuditActions.ServicePaused;
                break;
            case BillingServiceTransition.Resume:
                var today = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today;
                service.Resume(today, now);
                action = BillingAuditActions.ServiceResumed;
                break;
            default:
                // La transición del servicio va primero: un servicio ya cancelado falla acá sin tocar ningún cobro.
                service.Cancel(now);
                foreach (var charge in await charges.ListPendingByServiceAsync(service.Id, cancellationToken))
                {
                    charge.Cancel(now);
                }

                action = BillingAuditActions.ServiceCancelled;
                break;
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            action,
            BillingAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            StatusField,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}
