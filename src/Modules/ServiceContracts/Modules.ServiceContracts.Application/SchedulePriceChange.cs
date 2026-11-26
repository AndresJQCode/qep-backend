using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>El cuerpo de <c>POST /services/{id}/price-schedule</c>; <c>EffectiveFrom</c> nulo es el período de revisión.</summary>
public sealed record SchedulePriceChangeRequest(decimal? NewPrice, DateOnly? EffectiveFrom, string? Reason);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record SchedulePriceChangeCommand(
    Guid TenantId,
    Guid ServiceId,
    decimal NewPrice,
    DateOnly? EffectiveFrom,
    string? Reason,
    long ExpectedVersion)
    : ICommand<ServiceDetailDto>
{
    public static SchedulePriceChangeCommand From(
        Guid tenantId, Guid serviceId, SchedulePriceChangeRequest request, long expectedVersion) => new(
            tenantId,
            serviceId,
            request.NewPrice ?? 0m,
            request.EffectiveFrom,
            request.Reason,
            expectedVersion);
}

public sealed class SchedulePriceChangeValidator : AbstractValidator<SchedulePriceChangeCommand>
{
    public SchedulePriceChangeValidator(ITenantClock tenantClock)
    {
        RuleFor(command => command.NewPrice).MustBeAServicePrice();
        RuleFor(command => command.EffectiveFrom)
            .MustBeAReasonableReviewDate(tenantClock, command => command.TenantId);
        // El motivo vacío lo responde el dominio con billing.service.price_reason_required; acá sólo el largo.
        RuleFor(command => command.Reason).MaximumLength(ServicePriceChange.ReasonMaxLength);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Programa un precio desde un período sin generar (spec reajuste §6.4). Si ese período ya está en la ventana, o el contrato
/// está retenido y el cambio lo cubre, los cobros nacen en la misma transacción. Quien lo programa firma con su membresía activa.
/// </summary>
public sealed class SchedulePriceChangeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IMembershipDirectory membershipDirectory,
    IServiceContractRepository services,
    IChargeRepository charges,
    IServicePriceChangeRepository priceChanges,
    IServicePriceScheduleRepository schedules,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<SchedulePriceChangeCommand> validator)
    : ICommandHandler<SchedulePriceChangeCommand, ServiceDetailDto>
{
    private static readonly string[] ScheduledFields = ["newPrice", "effectiveFrom", "reason"];

    public async Task<ServiceDetailDto> HandleAsync(SchedulePriceChangeCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var service = await services.FindAsync(command.TenantId, new ServiceContractId(command.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);

        var pending = await schedules.FindPendingAsync(command.TenantId, service.Id, cancellationToken);
        var memberId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, command.TenantId, cancellationToken);
        var today = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today;
        var now = clock.UtcNow;
        var schedule = ServicePriceSchedule.Schedule(
            service, pending, command.NewPrice, command.EffectiveFrom, command.Reason, memberId, now);
        schedules.Add(schedule);
        PriceReviewGeneration.Run(service, schedule, today, now, charges, priceChanges);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ServicePriceScheduled,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            ScheduledFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}
