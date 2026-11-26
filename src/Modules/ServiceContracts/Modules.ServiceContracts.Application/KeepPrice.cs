using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record KeepPriceCommand(Guid TenantId, Guid ServiceId, long ExpectedVersion) : ICommand<ServiceDetailDto>;

public sealed class KeepPriceValidator : AbstractValidator<KeepPriceCommand>
{
    public KeepPriceValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// «Mantener precio» (spec reajuste §6.4): resuelve la revisión vigente sin tocar el precio y, si el contrato estaba retenido,
/// genera en la misma transacción. Sin motivo, porque no se guardaría en ningún lado: la auditoría registra la fecha nueva.
/// </summary>
public sealed class KeepPriceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IServiceContractRepository services,
    IChargeRepository charges,
    IServicePriceChangeRepository priceChanges,
    IServicePriceScheduleRepository schedules,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<KeepPriceCommand> validator)
    : ICommandHandler<KeepPriceCommand, ServiceDetailDto>
{
    private static readonly string[] KeptFields = ["nextReviewDate"];

    public async Task<ServiceDetailDto> HandleAsync(KeepPriceCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var service = await services.FindAsync(command.TenantId, new ServiceContractId(command.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);

        var today = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today;
        var now = clock.UtcNow;
        // El estado primero (cancelled, not_active), como al programar; el rechazo de abajo no guarda nada.
        service.KeepPrice(now);
        if (await schedules.FindPendingAsync(command.TenantId, service.Id, cancellationToken) is not null)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceScheduleAlreadyPending, "Cancel the pending price change before keeping the price.");
        }

        PriceReviewGeneration.Run(service, null, today, now, charges, priceChanges);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ServicePriceKept,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            KeptFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}
