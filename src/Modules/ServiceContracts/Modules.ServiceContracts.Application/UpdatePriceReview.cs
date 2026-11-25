using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary><c>PUT /services/{id}/price-review</c>: aquí los tres campos del reajuste son obligatorios.</summary>
/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record UpdatePriceReviewCommand(
    Guid TenantId,
    Guid ServiceId,
    string AdjustmentMode,
    decimal IpcExtraPoints,
    DateOnly NextReviewDate,
    long ExpectedVersion)
    : ICommand<ServiceDetailDto>
{
    // Un campo ausente viaja como un valor que su regla rechaza (-1 puntos, fecha por defecto): el 422 nombra el campo.
    public static UpdatePriceReviewCommand From(Guid tenantId, Guid serviceId, PriceReviewInput input, long expectedVersion) => new(
        tenantId,
        serviceId,
        input.AdjustmentMode ?? string.Empty,
        input.IpcExtraPoints ?? -1m,
        input.NextReviewDate ?? default,
        expectedVersion);
}

public sealed class UpdatePriceReviewValidator : AbstractValidator<UpdatePriceReviewCommand>
{
    public UpdatePriceReviewValidator()
    {
        RuleFor(command => command.AdjustmentMode).MustBeAnAdjustmentMode();
        RuleFor(command => command.IpcExtraPoints).MustBeIpcExtraPoints();
        // El techo de diez años lo pone el handler: depende de la fecha que el contrato ya tiene guardada.
        RuleFor(command => command.NextReviewDate).NotEmpty();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// La edición del reajuste (spec reajuste §6.4), permitida con el contrato activo o pausado. Puede liberar un retenido: pasarlo
/// a <c>Manual</c> genera el período «sin revisar», y correr la revisión más allá de él lo genera con el precio vigente.
/// </summary>
public sealed class UpdatePriceReviewHandler(
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
    IValidator<UpdatePriceReviewCommand> validator)
    : ICommandHandler<UpdatePriceReviewCommand, ServiceDetailDto>
{
    public async Task<ServiceDetailDto> HandleAsync(UpdatePriceReviewCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        if (!PriceReviewRules.TryParseMode(command.AdjustmentMode, out var mode))
        {
            throw new InvalidOperationException($"Mode '{command.AdjustmentMode}' passed validation but is not an adjustment mode.");
        }

        var service = await services.FindAsync(command.TenantId, new ServiceContractId(command.ServiceId), cancellationToken)
            ?? throw ServiceContractsNotFound.Service(command.ServiceId);
        ServiceVersion.EnsureCurrent(service, command.ExpectedVersion);

        var pending = await schedules.FindPendingAsync(command.TenantId, service.Id, cancellationToken);
        var today = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today;
        // El techo sólo se le exige a una fecha que cambia: «Mantener precio» repetido puede dejar la guardada más allá, y eso
        // no puede impedir editar el modo o los puntos.
        if (command.NextReviewDate != service.NextReviewDate && command.NextReviewDate > PriceReviewRules.LatestReviewDate(today))
        {
            throw new ValidationException(
                [new ValidationFailure(nameof(command.NextReviewDate), PriceReviewRules.LatestReviewDateMessage)]);
        }

        var now = clock.UtcNow;
        var previous = (Mode: service.AdjustmentMode, Points: service.IpcExtraPoints, Review: service.NextReviewDate);
        service.UpdatePriceReview(mode, command.IpcExtraPoints, command.NextReviewDate, now);
        PriceReview.EnsureNotAfterReview(service, pending);

        // Antes de generar: la generación puede mover la revisión, y eso no lo escribió la persona.
        var changedFields = new List<string>(3);
        if (previous.Mode != service.AdjustmentMode)
        {
            changedFields.Add("adjustmentMode");
        }

        if (previous.Points != service.IpcExtraPoints)
        {
            changedFields.Add("ipcExtraPoints");
        }

        if (previous.Review != service.NextReviewDate)
        {
            changedFields.Add("nextReviewDate");
        }

        PriceReviewGeneration.Run(service, pending, today, now, charges, priceChanges);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ServicePriceReviewUpdated,
            ServiceContractsAuditActions.Resources.Service,
            service.Id.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(service, cancellationToken);
    }
}
