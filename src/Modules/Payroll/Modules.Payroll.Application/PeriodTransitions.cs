using BuildingBlocks.Application;
using FluentValidation;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary><c>POST {p}/reopen</c>: el motivo es obligatorio (1 a 500 caracteres) y queda en el historial.</summary>
public sealed record ReopenRequest(string? Reason);

public sealed class ReopenRequestValidator : PayrollValidator<ReopenRequest>
{
    public const int ReasonMaxLength = 500;

    public ReopenRequestValidator() =>
        RuleFor(request => request.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason)).WithMessage("Escribe el motivo.")
            .Must(reason => reason is null || reason.Trim().Length <= ReasonMaxLength)
            .WithMessage($"El motivo no puede pasar de {ReasonMaxLength} caracteres.");
}

public sealed record ApprovePeriodCommand(Guid TenantId, Guid PeriodId, string? IfMatch) : ICommand<PayrollPeriodDetailDto>;

public sealed record ClosePeriodCommand(Guid TenantId, Guid PeriodId, string? IfMatch) : ICommand<PayrollPeriodDetailDto>;

public sealed record ReopenPeriodCommand(Guid TenantId, Guid PeriodId, ReopenRequest Request, string? IfMatch)
    : ICommand<PayrollPeriodDetailDto>;

/// <summary>Lo que repiten aprobar, cerrar y reabrir: autorizar, <c>If-Match</c>, cargar y la versión.</summary>
internal static class PeriodTransition
{
    public static async Task<PayrollPeriod> LoadAsync(
        IPayrollPeriodRepository periods, Guid tenantId, Guid periodId, long expectedVersion, CancellationToken cancellationToken)
    {
        var period = await periods.FindAsync(tenantId, new PayrollPeriodId(periodId), cancellationToken)
            ?? throw PayrollNotFound.Period(periodId);
        PayrollSupport.EnsureVersion(period.Version, expectedVersion);
        return period;
    }
}

/// <summary>
/// Aprueba un período Liquidado. El <see cref="ApprovalContext"/> sale de la configuración (doble control), de la huella de Hr de hoy
/// (<c>sourceChanged</c>), de los netos guardados y del estado de la primera quincena. Con doble control, quien liquidó por última vez
/// no aprueba.
/// </summary>
public sealed class ApprovePeriodHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IPayrollPeriodRepository periods,
    IPayrollResultStore resultStore,
    PeriodApprovals approvals,
    PeriodActors actors,
    PeriodComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher)
    : ICommandHandler<ApprovePeriodCommand, PayrollPeriodDetailDto>
{
    public async Task<PayrollPeriodDetailDto> HandleAsync(ApprovePeriodCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodApprove);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);
        var period = await PeriodTransition.LoadAsync(periods, command.TenantId, command.PeriodId, expectedVersion, cancellationToken);

        var actor = await actors.CurrentAsync(command.TenantId, cancellationToken);
        var state = await approvals.StateAsync(period, withSource: true, cancellationToken);
        var anyNegativeNet = (await resultStore.ListAsync(command.TenantId, period.Id, cancellationToken))
            .Any(result => result.Liquidation.NegativeNet);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        period.Approve(state.ContextFor(actor, anyNegativeNet), now);
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.PeriodApproved,
            PayrollAuditActions.Resources.Period, period.Id.ToString(), ["status"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(period, actor, actors.CallerPermissions(), cancellationToken);
    }
}

public sealed class ClosePeriodHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IPayrollPeriodRepository periods,
    PeriodActors actors,
    PeriodComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher)
    : ICommandHandler<ClosePeriodCommand, PayrollPeriodDetailDto>
{
    public async Task<PayrollPeriodDetailDto> HandleAsync(ClosePeriodCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);
        var period = await PeriodTransition.LoadAsync(periods, command.TenantId, command.PeriodId, expectedVersion, cancellationToken);

        var actor = await actors.CurrentAsync(command.TenantId, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        period.Close(actor, now);
        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.PeriodClosed,
            PayrollAuditActions.Resources.Period, period.Id.ToString(), ["status"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(period, actor, actors.CallerPermissions(), cancellationToken);
    }
}

/// <summary>Reabre un período Aprobado o Cerrado, con el motivo. Reabrir una primera quincena desactualiza la segunda (R-14).</summary>
public sealed class ReopenPeriodHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IPayrollPeriodRepository periods,
    PeriodActors actors,
    PeriodComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher,
    IValidator<ReopenRequest> validator)
    : ICommandHandler<ReopenPeriodCommand, PayrollPeriodDetailDto>
{
    public async Task<PayrollPeriodDetailDto> HandleAsync(ReopenPeriodCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        var period = await PeriodTransition.LoadAsync(periods, command.TenantId, command.PeriodId, expectedVersion, cancellationToken);

        var actor = await actors.CurrentAsync(command.TenantId, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        period.Reopen(actor, command.Request.Reason!.Trim(), now);

        if (period.Half == PayrollHalf.First)
        {
            var second = await periods.FindTrackedByStartAsync(
                command.TenantId, period.CompanyId, new DateOnly(period.StartDate.Year, period.StartDate.Month, 16), cancellationToken);
            second?.MarkOutdated(actor, now);
        }

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.PeriodReopened,
            PayrollAuditActions.Resources.Period, period.Id.ToString(), ["status", "reason"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(period, actor, actors.CallerPermissions(), cancellationToken);
    }
}
