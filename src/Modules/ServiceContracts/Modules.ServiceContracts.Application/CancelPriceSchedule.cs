using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record CancelPriceScheduleCommand(Guid TenantId, Guid ServiceId, long ExpectedVersion) : ICommand<ServiceDetailDto>;

public sealed class CancelPriceScheduleValidator : AbstractValidator<CancelPriceScheduleCommand>
{
    public CancelPriceScheduleValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Cancela el cambio de precio pendiente. No genera nada: quitar una decisión no resuelve ninguna revisión, y lo que la revisión
/// pida lo vuelve a mirar la corrida diaria.
/// </summary>
public sealed class CancelPriceScheduleHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractRepository services,
    IServicePriceScheduleRepository schedules,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ServiceDetailComposer composer,
    IValidator<CancelPriceScheduleCommand> validator)
    : ICommandHandler<CancelPriceScheduleCommand, ServiceDetailDto>
{
    private static readonly string[] CancelledFields = ["scheduleId"];

    public async Task<ServiceDetailDto> HandleAsync(CancelPriceScheduleCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var service = await services.FindAsync(command.TenantId, new ServiceContractId(command.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);

        // Sin pendiente es un 404 (spec reajuste §8). El dominio respondería el mismo código como 422, así que se mira antes.
        var pending = await schedules.FindPendingAsync(command.TenantId, service.Id, cancellationToken)
            ?? throw ServiceContractsNotFound.PriceSchedule(command.ServiceId);
        var now = clock.UtcNow;
        pending.Cancel(service, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ServicePriceScheduleCancelled,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            CancelledFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}
