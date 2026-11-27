using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Control;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

public sealed record RejectAdjustmentCommand(Guid TenantId, Guid AdjustmentId, long Version, string Note) : ICommand<AdjustmentDto>
{
    public static RejectAdjustmentCommand From(Guid tenantId, Guid adjustmentId, RejectAdjustmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(tenantId, adjustmentId, request.Version, request.Note ?? string.Empty);
    }
}

public sealed class RejectAdjustmentValidator : AbstractValidator<RejectAdjustmentCommand>
{
    public RejectAdjustmentValidator()
    {
        RuleFor(command => command.Note)
            .Must(note => !string.IsNullOrWhiteSpace(note) && note.Trim().Length <= Adjustment.NoteMaxLength)
            .WithMessage($"The note is required and has at most {Adjustment.NoteMaxLength} characters.");
    }
}

public sealed class RejectAdjustmentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IAdjustmentRepository adjustments,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    AdjustmentDtoComposer composer,
    IIncidentRepository incidents,
    WmsCaller caller,
    IValidator<RejectAdjustmentCommand> validator)
    : ICommandHandler<RejectAdjustmentCommand, AdjustmentDto>
{
    public async Task<AdjustmentDto> HandleAsync(RejectAdjustmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var adjustment = await adjustments.GetAsync(command.TenantId, command.AdjustmentId, cancellationToken)
            ?? throw WmsNotFound.Adjustment(command.AdjustmentId);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, adjustment.OwnerId.Value, adjustment.WarehouseId.Value, cancellationToken);
        WmsVersions.Ensure(adjustment.Version, command.Version);

        var now = clock.UtcNow;
        adjustment.Reject(executionContext.SubjectId, command.Note, now);

        // La CountVariance que este ajuste seguía se rechaza con él; sin novedad detrás, no se pide membresía.
        if (await incidents.FindByAdjustmentAsync(command.TenantId, adjustment.Id, cancellationToken) is { } variance)
        {
            variance.RejectByAdjustment(await caller.RequireAsync(command.TenantId, cancellationToken), command.Note, now);
        }

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            AdjustmentRejected,
            Resources.Adjustment,
            adjustment.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = adjustment.OwnerId.Value.ToString(),
                ["number"] = adjustment.Number,
                ["note"] = adjustment.RejectNote,
            },
            now);

        var dto = await composer.ComposeAsync(adjustment, null, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }
}
