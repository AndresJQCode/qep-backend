using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>
/// The dependency between quincenas that a vacation paid in advance creates (spec «Ciclo de vida y bloqueos», rules 4 and 5): a
/// quincena with days of a vacation paid elsewhere cannot be liquidated before it, and liquidating the paying one again changes what
/// the following ones read.
/// </summary>
public sealed class VacationPayments(IPayrollPeriodRepository periods, LiquidationPlanner planner, VacationLocks locks)
{
    private static readonly string[] Months =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
    ];

    /// <summary>
    /// Rule 5: every vacation of the plan paid elsewhere is paid (Liquidated, Approved or Closed, not Outdated, and a Liquidated
    /// one without SourceChanged). Else 422 payroll.vacation.not_paid_yet naming the paying quincena.
    /// </summary>
    public async Task EnsurePaidAsync(LiquidationPlan plan, Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Draft, Reopened, Outdated or without the photo of the vacation: the plan already carries the input with no photo.
        if (plan.Unpaid.OrderBy(owed => owed.PaidInStart).ThenBy(owed => owed.VacationId).FirstOrDefault() is { } unpaid)
        {
            throw NotPaidYet(unpaid.PaidInStart, unpaid.PaidInEnd);
        }

        // A Liquidated paying quincena whose source changed would be liquidated again with another photo: it is not paid «al día».
        foreach (var start in plan.PayingStarts)
        {
            var paying = await periods.FindByStartAsync(tenantId, companyId, start, cancellationToken);
            if (paying is { Status: PayrollPeriodStatus.Liquidated, Fingerprint: { } stored }
                && (await planner.PlanAsync(paying, cancellationToken)).Fingerprint != stored)
            {
                throw NotPaidYet(paying.StartDate, paying.EndDate);
            }
        }
    }

    /// <summary>
    /// Rule 4: with the new photos, no Approved or Closed later quincena changes (same comparison as VacationLocks); else 422
    /// payroll.vacation.paid_period_locked naming it. Then MarkOutdated on the later Liquidated or Reopened ones.
    /// </summary>
    public async Task ApplyNewPhotosAsync(
        PayrollPeriod paying,
        IReadOnlyList<EmployeeResult> results,
        PayrollActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paying);
        ArgumentNullException.ThrowIfNull(results);

        var photos = results
            .SelectMany(result => result.Liquidation.VacationPhotos.Select(photo => (result.EmployeeId, Photo: photo)))
            .ToList();
        if (photos.Count == 0)
        {
            return;
        }

        // What each employee's following quincenas would read once the new photos are stored.
        var overrides = new PlanOverrides(null, photos.ToDictionary(item => item.Photo.VacationId, item => item.Photo));
        var later = photos
            .SelectMany(item => VacationReach.QuincenaStarts(item.Photo.Quincenas)
                .Where(start => start > paying.StartDate)
                .Select(start => (Start: start, item.EmployeeId)))
            .Distinct()
            .OrderBy(item => item.Start)
            .ToList();

        foreach (var (start, employeeId) in later)
        {
            var reached = await periods.FindByStartAsync(paying.TenantId, paying.CompanyId, start, cancellationToken);
            if (reached is { Status: PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed }
                && await locks.WouldChangeAsync(reached, employeeId, overrides, cancellationToken))
            {
                throw new PayrollDomainException(
                    PayrollErrorCodes.VacationPaidPeriodLocked,
                    $"Volver a liquidar esta quincena cambiaría lo que ya aprobó la quincena {Label(reached.StartDate, reached.EndDate)}: reábrela primero.");
            }
        }

        foreach (var start in later.Select(item => item.Start).Distinct())
        {
            var reached = await periods.FindTrackedByStartAsync(paying.TenantId, paying.CompanyId, start, cancellationToken);
            if (reached is { Status: PayrollPeriodStatus.Liquidated or PayrollPeriodStatus.Reopened })
            {
                reached.MarkOutdated(actor, now);
            }
        }
    }

    private static PayrollDomainException NotPaidYet(DateOnly start, DateOnly end) =>
        new(
            PayrollErrorCodes.VacationNotPaidYet,
            $"La quincena {Label(start, end)} todavía no paga las vacaciones de esta quincena: liquídala primero.");

    // «del 1 al 15 de diciembre de 2026»: a quincena never crosses a month.
    private static string Label(DateOnly start, DateOnly end) =>
        $"del {start.Day} al {end.Day} de {Months[end.Month - 1]} de {end.Year}";
}
