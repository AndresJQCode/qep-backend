using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public enum ServiceContractTransition
{
    Pause = 1,
    Resume = 2,
    Cancel = 3,
}

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ChangeServiceStatusCommand(
    Guid TenantId,
    Guid ServiceId,
    ServiceContractTransition Transition,
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
/// Pausar, reanudar y cancelar (spec §3.2 y reajuste §6.4). Pausar no toca los cobros: los <c>Pending</c> siguen pendientes y
/// avisándose. Cancelar pasa <b>todos</b> los <c>Pending</c> a <c>Cancelled</c> y cancela el cambio de precio programado, en la
/// misma transacción. Pausar un retenido guarda en el dominio los períodos retenidos y quita la retención; cancelar los
/// descarta. Reanudar escribe la fecha de reactivación: los períodos que cayeron en la pausa no se cobran y el worker retoma
/// desde la primera ancla de ahí en adelante. Lo retenido antes de la pausa sigue retenido y, si ya no es de revisión, se
/// genera al reanudar, en la misma transacción.
/// </summary>
public sealed class ChangeServiceStatusHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IServiceContractRepository services,
    IChargeRepository charges,
    IServicePriceScheduleRepository schedules,
    IServicePriceChangeRepository priceChanges,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<ChangeServiceStatusCommand> validator)
    : ICommandHandler<ChangeServiceStatusCommand, ServiceDetailDto>
{
    private static readonly string[] StatusField = ["status"];

    public async Task<ServiceDetailDto> HandleAsync(ChangeServiceStatusCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Primero el servicio, acotado al tenant: ListPendingByServiceAsync no lleva tenant y cancelaría cobros ajenos.
        var service = await services.FindAsync(command.TenantId, new ServiceContractId(command.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);

        var now = clock.UtcNow;
        string action;
        switch (command.Transition)
        {
            case ServiceContractTransition.Pause:
                service.Pause(await TodayAsync(command.TenantId, cancellationToken), now);
                action = ServiceContractsAuditActions.ServicePaused;
                break;
            case ServiceContractTransition.Resume:
                var today = await TodayAsync(command.TenantId, cancellationToken);
                service.Resume(today, now);
                // Lo que quedó retenido antes de la pausa puede haber dejado de ser de revisión (pasó a Manual o se corrió la
                // fecha mientras estaba pausado): entonces nace al reanudar, en esta misma transacción.
                if (service.CarriedHeldPeriods.Count > 0)
                {
                    var pending = await schedules.FindPendingAsync(command.TenantId, service.Id, cancellationToken);
                    PriceReviewGeneration.Run(service, pending, today, now, charges, priceChanges);
                }

                action = ServiceContractsAuditActions.ServiceResumed;
                break;
            default:
                // La transición del servicio va primero: un servicio ya cancelado falla acá sin tocar ningún cobro.
                service.Cancel(now);
                foreach (var charge in await charges.ListPendingByServiceAsync(service.Id, cancellationToken))
                {
                    charge.Cancel(now);
                }

                var scheduled = await schedules.FindPendingAsync(command.TenantId, service.Id, cancellationToken);
                scheduled?.Cancel(service, now);
                action = ServiceContractsAuditActions.ServiceCancelled;
                break;
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            action,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            StatusField,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }

    private async Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await tenantClock.GetAsync(tenantId, cancellationToken)).Today;
}
