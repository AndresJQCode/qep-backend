using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary><c>POST {p}/liquidate</c>. Hasta el proyecto 2 el sistema no conoce las ausencias: quien liquida lo confirma (R-27).</summary>
public sealed record LiquidateRequest(bool? ConfirmNoAbsences);

public sealed record LiquidatePeriodCommand(Guid TenantId, Guid PeriodId, LiquidateRequest? Request, string? IfMatch)
    : ICommand<PayrollPeriodDetailDto>;

/// <summary>
/// Liquida (o vuelve a liquidar) un período. Orden: autorizar, <c>If-Match</c>, cargar, versión (412), estado, confirmación de
/// ausencias, la primera quincena (R-14), el plan (bloqueados y nadie que liquidar), el motor por empleado, los resultados, el
/// período y, en una primera, la segunda desactualizada. Las vacaciones pagadas en otra quincena se esperan antes del motor (
/// <c>not_paid_yet</c>) y las fotos nuevas se aplican a las siguientes antes de guardar. Un solo <c>SaveChanges</c>: o queda todo o no queda nada.
/// </summary>
public sealed class LiquidatePeriodHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IPayrollPeriodRepository periods,
    IPayrollResultStore resultStore,
    LiquidationPlanner planner,
    VacationPayments vacationPayments,
    PeriodApprovals approvals,
    PeriodActors actors,
    PeriodComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher)
    : ICommandHandler<LiquidatePeriodCommand, PayrollPeriodDetailDto>
{
    public async Task<PayrollPeriodDetailDto> HandleAsync(LiquidatePeriodCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);

        var period = await periods.FindAsync(command.TenantId, new PayrollPeriodId(command.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(command.PeriodId);
        PayrollSupport.EnsureVersion(period.Version, expectedVersion);

        if (period.Status is PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.PeriodInvalidTransition, "An approved or closed period is reopened before it is liquidated again.");
        }

        if (command.Request?.ConfirmNoAbsences != true)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.LiquidationAbsencesUnconfirmed, "Confirm that no employee had absences in this period before liquidating.");
        }

        var state = await approvals.StateAsync(period, withSource: false, cancellationToken);
        if (!state.FirstHalfLiquidated)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.PeriodFirstHalfNotLiquidated, "The first half of the month must be liquidated and up to date first.");
        }

        var plan = await planner.PlanAsync(period, cancellationToken);
        if (plan.Blocked.Count > 0)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.LiquidationBlocked, "Some employees lack data the calculation needs: fix them and liquidate again.");
        }

        // A quincena with days of a vacation paid elsewhere waits for the paying one (spec rule 5), before the engine reads a missing photo.
        await vacationPayments.EnsurePaidAsync(plan, command.TenantId, period.CompanyId, cancellationToken);

        if (plan.Liquidable.Count == 0)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.LiquidationNobodyToLiquidate, "There is nobody to liquidate in this period.");
        }

        var liquidated = plan.Liquidable
            .Select(planned => ToResult(planned, PayrollEngine.Liquidate(planned.Input)))
            .ToList();
        await resultStore.ReplaceAllAsync(command.TenantId, period.Id, liquidated, cancellationToken);

        var actor = await actors.CurrentAsync(command.TenantId, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        period.RecordLiquidation(
            actor,
            absencesConfirmed: true,
            PayrollResultMapper.ToJson(plan.Legal),
            plan.Legal.ResolvedAt,
            PayrollEngine.Version,
            plan.Fingerprint,
            PeriodExcluded.ToJson(plan.Excluded),
            now);

        // R-14: liquidating the first half again changes what the second one reads.
        if (period.Half == PayrollHalf.First)
        {
            var second = await periods.FindTrackedByStartAsync(
                command.TenantId, period.CompanyId, new DateOnly(period.StartDate.Year, period.StartDate.Month, 16), cancellationToken);
            second?.MarkOutdated(actor, now);
        }

        // The photos just written are what the following quincenas read: refuse if an Approved one would change (rule 4), outdate the rest.
        await vacationPayments.ApplyNewPhotosAsync(period, liquidated, actor, now, cancellationToken);

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.PeriodLiquidated,
            PayrollAuditActions.Resources.Period, period.Id.ToString(), ["absencesConfirmed", "employees"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(period, actor, actors.CallerPermissions(), cancellationToken);
    }

    private static EmployeeResult ToResult(PlannedEmployee planned, EmployeeLiquidation liquidation)
    {
        var assignment = planned.Eligibility.AssignmentAtEnd;

        // What each recurring concept applied (R-12): the accumulated of the next period reads it from the last photo.
        var applied = new Dictionary<Guid, decimal>();
        foreach (var line in liquidation.Lines.Where(line => line.Code.StartsWith("recurring:", StringComparison.Ordinal)))
        {
            var id = Guid.Parse(line.Code["recurring:".Length..]);
            applied[id] = applied.GetValueOrDefault(id) + line.Amount;
        }

        return new EmployeeResult(
            planned.Source.EmployeeId,
            planned.Source.DocumentType,
            planned.Source.DocumentNumber,
            LiquidationPlanner.FullNameOf(planned.Source),
            assignment?.PositionName,
            assignment is null ? null : $"{assignment.WorkplaceName} - {assignment.SiteName}",
            liquidation,
            planned.Fingerprint,
            applied);
    }
}
