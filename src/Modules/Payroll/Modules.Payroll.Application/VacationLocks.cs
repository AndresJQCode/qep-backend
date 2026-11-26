using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// What a write of vacations does to the quincenas they reach (spec «Ciclo de vida y bloqueos», rules 1 and 2). The lock is by
/// fingerprint, not by status: an Approved or Closed quincena is a snapshot, and an edit is refused only when it would change what
/// that snapshot calculated.
/// </summary>
public sealed class VacationLocks(IPayrollPeriodRepository periods, IPayrollResultStore results, LiquidationPlanner planner)
{
    internal const string NotEditableDetail = "Reabre la quincena para cambiar estas vacaciones.";

    /// <summary>
    /// Rule 1: for every Approved or Closed quincena the old or the new range reaches, the employee's fingerprint with the
    /// vacations as they would be must equal the stored one or today's without the change; else 422 payroll.period.not_editable.
    /// The second comparison matters: Hr may have moved something since the approval, and then an edit that does not touch what
    /// the quincena calculates must still pass (comparing only with the stored fingerprint would block every edit forever).
    /// </summary>
    public async Task EnsureUnlockedAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        IReadOnlyList<Vacation> before,
        IReadOnlyList<Vacation> after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        foreach (var start in ReachedStarts(Changed(before, after)))
        {
            var period = await periods.FindByStartAsync(tenantId, companyId, start, cancellationToken);
            if (period is not { Status: PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed })
            {
                continue;
            }

            // What the planner would read as the vacations of this month: only those that overlap it, as the real read does.
            var inMonth = after.Where(vacation => VacationReach.IsInputOf(vacation, period.StartDate, period.EndDate)).ToList();
            var overrides = new PlanOverrides(new VacationListOverride(employeeId, inMonth), PlanOverrides.None.Photos);
            if (await WouldChangeAsync(period, employeeId, overrides, cancellationToken))
            {
                throw new PayrollDomainException(PayrollErrorCodes.PeriodNotEditable, NotEditableDetail);
            }
        }
    }

    /// <summary>
    /// Would the employee's calculation of this Approved or Closed quincena change under <paramref name="overrides"/>? Only when its
    /// fingerprint with them differs from the stored one <b>and</b> from today's without them: Hr may have moved something since the
    /// approval, and an unrelated change must not block every edit forever.
    /// </summary>
    internal async Task<bool> WouldChangeAsync(
        PayrollPeriod period, Guid employeeId, PlanOverrides overrides, CancellationToken cancellationToken)
    {
        var stored = await results.FindAsync(period.TenantId, period.Id, employeeId, cancellationToken);
        if (stored is null)
        {
            return false;
        }

        var withChange = FingerprintOf(await planner.PlanAsync(period, overrides, cancellationToken), employeeId);
        if (withChange is null || withChange == stored.Fingerprint)
        {
            return false;
        }

        return withChange != FingerprintOf(await planner.PlanAsync(period, cancellationToken), employeeId);
    }

    /// <summary>
    /// Rule 2: MarkOutdated on every reached quincena that is Liquidated or Reopened (tracked); Draft is a no-op, Approved and
    /// Closed are skipped (rule 1 already refused whatever would change them, and an unchanged snapshot stays as it is).
    /// </summary>
    public async Task MarkReachedOutdatedAsync(
        Guid tenantId,
        Guid companyId,
        IEnumerable<Vacation> touched,
        PayrollActor actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(touched);
        foreach (var start in ReachedStarts(touched))
        {
            var period = await periods.FindTrackedByStartAsync(tenantId, companyId, start, cancellationToken);
            if (period is { Status: PayrollPeriodStatus.Liquidated or PayrollPeriodStatus.Reopened })
            {
                period.MarkOutdated(actor, now);
            }
        }
    }

    // The vacations that are in one list only or whose data differ: the old and the new range of the write.
    private static List<Vacation> Changed(IReadOnlyList<Vacation> before, IReadOnlyList<Vacation> after)
    {
        var changed = new List<Vacation>();
        var afterById = after.ToDictionary(vacation => vacation.Id);
        foreach (var old in before)
        {
            if (!afterById.TryGetValue(old.Id, out var current) || !SameData(old, current))
            {
                changed.Add(old);
            }
        }

        var beforeIds = before.Select(vacation => vacation.Id).ToHashSet();
        changed.AddRange(after.Where(vacation => !beforeIds.Contains(vacation.Id) || !SameData(before.First(old => old.Id == vacation.Id), vacation)));
        return changed;
    }

    private static bool SameData(Vacation a, Vacation b) =>
        (a.Origin, a.StartDate, a.EndDate, a.BusinessDays, a.ReturnDate, a.PreviousMonthIbc)
        == (b.Origin, b.StartDate, b.EndDate, b.BusinessDays, b.ReturnDate, b.PreviousMonthIbc)
        && a.ExternalMonths.SequenceEqual(b.ExternalMonths);

    private static SortedSet<DateOnly> ReachedStarts(IEnumerable<Vacation> vacations) =>
        [.. vacations.SelectMany(VacationReach.QuincenaStarts)];

    private static string? FingerprintOf(LiquidationPlan plan, Guid employeeId) =>
        plan.Liquidable.FirstOrDefault(planned => planned.Source.EmployeeId == employeeId)?.Fingerprint;
}
