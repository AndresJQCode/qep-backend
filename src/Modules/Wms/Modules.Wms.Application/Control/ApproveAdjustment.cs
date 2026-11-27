using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

public sealed record ApproveAdjustmentCommand(Guid TenantId, Guid AdjustmentId, long Version) : ICommand<AdjustmentDto>;

/// <summary>Aprobar no toca el ledger: basta la versión como token optimista (dos aprobaciones, o una aprobación contra un rechazo,
/// dejan una sola ganadora y la otra recibe 412).</summary>
public sealed class ApproveAdjustmentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IAdjustmentRepository adjustments,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    AdjustmentDtoComposer composer,
    IIncidentRepository incidents,
    WmsCaller caller)
    : ICommandHandler<ApproveAdjustmentCommand, AdjustmentDto>
{
    public async Task<AdjustmentDto> HandleAsync(ApproveAdjustmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, ct: cancellationToken);

        var adjustment = await adjustments.GetAsync(command.TenantId, command.AdjustmentId, cancellationToken)
            ?? throw WmsNotFound.Adjustment(command.AdjustmentId);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.AdjustmentApprove, adjustment.OwnerId.Value, adjustment.WarehouseId.Value, cancellationToken);
        WmsVersions.Ensure(adjustment.Version, command.Version);

        // Un ajuste que propuso un conteo no lo aprueba quien contó ni quien reportó el faltante (DA-17, DA-47). Se comparan
        // membresías (WmsCaller), nunca SubjectId. Sin novedad detrás, el ajuste se aprueba como siempre y no pide membresía.
        if (await incidents.FindByAdjustmentAsync(command.TenantId, adjustment.Id, cancellationToken) is { } variance)
        {
            var member = await caller.RequireAsync(command.TenantId, cancellationToken);
            variance.EnsureNotInvolvedInApproval(
                member, await incidents.ListReportersOfChildrenAsync(command.TenantId, variance.Id, cancellationToken));
        }

        var now = clock.UtcNow;
        adjustment.Approve(executionContext.SubjectId, now);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            AdjustmentApproved,
            Resources.Adjustment,
            adjustment.Id.ToString(),
            new Dictionary<string, string?> { ["ownerId"] = adjustment.OwnerId.Value.ToString(), ["number"] = adjustment.Number },
            now);

        var dto = await composer.ComposeAsync(adjustment, null, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }
}
