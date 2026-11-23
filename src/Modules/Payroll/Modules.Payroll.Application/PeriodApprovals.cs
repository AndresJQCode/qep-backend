using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Lo que un período necesita de afuera para decidir si se puede liquidar y aprobar (R-14, R-16).</summary>
/// <param name="FirstHalfLiquidated">En una primera quincena siempre verdadero; en una segunda, la primera está liquidada (o más) y al día.</param>
/// <param name="FirstHalfApproved">En una primera siempre verdadero; en una segunda, la primera está aprobada o cerrada.</param>
/// <param name="SourceChanged">Sólo en un Liquidado: la huella de hoy difiere de la guardada.</param>
public sealed record PeriodApprovalState(bool DualControl, bool FirstHalfLiquidated, bool FirstHalfApproved, bool SourceChanged)
{
    public ApprovalContext ContextFor(PayrollActor caller, bool anyNegativeNet) =>
        new(caller, DualControl, SourceChanged, anyNegativeNet, FirstHalfApproved);
}

/// <summary>Arma el <see cref="PeriodApprovalState"/> de un período: lo comparten el detalle (<c>can</c>) y los casos de uso de liquidar y aprobar.</summary>
public sealed class PeriodApprovals(
    ICompanyPayrollSettingsRepository settings, IPayrollPeriodRepository periods, LiquidationPlanner planner)
{
    /// <param name="withSource">Calcular <c>SourceChanged</c> lee todo Hr otra vez: liquidar no lo necesita.</param>
    public async Task<PeriodApprovalState> StateAsync(PayrollPeriod period, bool withSource, CancellationToken cancellationToken)
    {
        var dualControl = (await settings.FindAsync(period.TenantId, period.CompanyId, cancellationToken))?.DualControl
            ?? CompanyPayrollSettings.DefaultDualControl;

        // R-14: la segunda quincena depende de la primera del mismo mes.
        var firstHalf = period.Half == PayrollHalf.Second
            ? await periods.FindByStartAsync(
                period.TenantId, period.CompanyId, new DateOnly(period.StartDate.Year, period.StartDate.Month, 1), cancellationToken)
            : null;
        var firstApproved = period.Half == PayrollHalf.First
            || firstHalf is { Status: PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed };
        var firstLiquidated = period.Half == PayrollHalf.First
            || firstHalf is { Outdated: false, Status: PayrollPeriodStatus.Liquidated or PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed };

        // Approved and Closed are a snapshot: Hr no longer decides (R-16).
        var sourceChanged = withSource
            && period.Status == PayrollPeriodStatus.Liquidated
            && period.Fingerprint is { } stored
            && (await planner.PlanAsync(period, cancellationToken)).Fingerprint != stored;

        return new PeriodApprovalState(dualControl, firstLiquidated, firstApproved, sourceChanged);
    }
}
