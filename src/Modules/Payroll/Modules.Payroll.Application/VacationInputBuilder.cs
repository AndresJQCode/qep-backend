using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>A vacation whose paying quincena has not paid it yet (spec rule 5): the following quincenas cannot be liquidated until it does.</summary>
public sealed record UnpaidVacation(Guid EmployeeId, Guid VacationId, DateOnly PaidInStart, DateOnly PaidInEnd);

/// <param name="PaidElsewhere">The start of each quincena whose stored photo these inputs read: a quincena that is only Liquidated must still be what it calculated.</param>
public sealed record VacationPlan(
    IReadOnlyList<VacationInput> Inputs, IReadOnlyList<UnpaidVacation> Unpaid, IReadOnlyList<DateOnly> PaidElsewhere);

/// <summary>
/// De las vacaciones registradas de un empleado, lo que el motor lee en una quincena (spec «Entrada del motor y huella»). Lee las
/// quincenas que pagan y sus resultados, pero nunca llama al planificador: el «pagada» que mira acá es el estado guardado, y el
/// <c>SourceChanged</c> se mira al liquidar.
/// </summary>
public sealed class VacationInputBuilder(IPayrollPeriodRepository periods, IPayrollResultStore results)
{
    private const string IbcLineCode = "ibc";

    /// <param name="photoOverrides">By vacation id: the photo to read instead of the stored one (what-if of a paying quincena).</param>
    public async Task<VacationPlan> BuildAsync(
        PayrollPeriod period,
        HrEmployeeSource employee,
        HrContractSource contract,
        IReadOnlyList<Vacation> vacations,
        IReadOnlyDictionary<Guid, VacationPhoto> photoOverrides,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(vacations);
        ArgumentNullException.ThrowIfNull(photoOverrides);

        var inputs = new List<VacationInput>();
        var unpaid = new List<UnpaidVacation>();
        var paidElsewhere = new List<DateOnly>();
        foreach (var vacation in vacations
            .Where(item => VacationReach.IsInputOf(item, period.StartDate, period.EndDate))
            .OrderBy(item => item.StartDate)
            .ThenBy(item => item.Id.Value))
        {
            // The days outside the contract are never carried: whatever the registered dates say, the net worked days of the
            // quincena cannot go negative. The employee is blocked for them (WithVacations) so a liquidation never gets here.
            var from = vacation.StartDate > contract.StartDate ? vacation.StartDate : contract.StartDate;
            var to = contract.EndedOn is { } ended && ended < vacation.EndDate ? ended : vacation.EndDate;
            if (to < from)
            {
                continue;
            }

            var (input, owed, readFrom) = await BuildOneAsync(period, employee, contract, vacation, from, to, photoOverrides, cancellationToken);
            inputs.Add(input);
            if (owed is not null)
            {
                unpaid.Add(owed);
            }

            if (readFrom is { } payingStart && !paidElsewhere.Contains(payingStart))
            {
                paidElsewhere.Add(payingStart);
            }
        }

        return new VacationPlan(inputs, unpaid, paidElsewhere);
    }

    private async Task<(VacationInput Input, UnpaidVacation? Unpaid, DateOnly? ReadFrom)> BuildOneAsync(
        PayrollPeriod period,
        HrEmployeeSource employee,
        HrContractSource contract,
        Vacation vacation,
        DateOnly from,
        DateOnly to,
        IReadOnlyDictionary<Guid, VacationPhoto> photoOverrides,
        CancellationToken cancellationToken)
    {
        var monthStart = new DateOnly(period.StartDate.Year, period.StartDate.Month, 1);
        var monthEnd = new DateOnly(monthStart.Year, monthStart.Month, DateTime.DaysInMonth(monthStart.Year, monthStart.Month));
        var paying = VacationReach.PayingQuincena(vacation);
        var paidHere = paying is { } quincena && quincena.Start == period.StartDate;
        var external = vacation.Origin == VacationOrigin.External
            ? vacation.ExternalMonths.FirstOrDefault(month => month.Month == monthStart)
            : null;

        // Review Focus 4: a quincena that does not pay reads the dates clipped to its own window and no business days, so editing
        // a part of the vacation that does not change its calculation does not change its fingerprint. What Siigo paid for the
        // month divides by the days of the whole month, so then the end is the end of the month.
        var start = paidHere ? from : Max(from, monthStart);
        var end = paidHere ? to : Min(to, external?.VacationPay is not null ? monthEnd : period.EndDate);
        var businessDays = paidHere ? vacation.BusinessDays : 0;

        if (vacation.Origin == VacationOrigin.External)
        {
            var salary = SalaryOn(contract, vacation.StartDate);
            var input = new VacationInput(
                vacation.Id.Value, vacation.Origin, start, end, businessDays, PaidHere: false,
                salary.Amount, salary.EffectiveDate, vacation.PreviousMonthIbc!.Value, VacationIbcSource.Typed,
                external?.VacationPay,
                external is null
                    ? VacationAdvance.Zero
                    : new VacationAdvance(external.HealthAdvance, external.PensionAdvance, external.SolidarityFundAdvance));
            return (input, null, null);
        }

        if (paidHere)
        {
            var salary = SalaryOn(contract, vacation.StartDate);
            var (ibc, source) = await PreviousIbcAsync(period, employee, contract, vacation, cancellationToken);
            return (
                new VacationInput(
                    vacation.Id.Value, vacation.Origin, start, end, businessDays, PaidHere: true,
                    salary.Amount, salary.EffectiveDate, ibc, source, null, VacationAdvance.Zero),
                null,
                null);
        }

        // Paid by another quincena: what that one froze is what this one reads.
        var photo = await PhotoAsync(period, employee, vacation, paying!.Value.Start, photoOverrides, cancellationToken);
        if (photo is null)
        {
            var zero = new VacationInput(
                vacation.Id.Value, vacation.Origin, start, end, businessDays, PaidHere: false,
                0m, default, 0m, VacationIbcSource.Salary, null, VacationAdvance.Zero);
            return (zero, new UnpaidVacation(employee.EmployeeId, vacation.Id.Value, paying.Value.Start, paying.Value.End), null);
        }

        var month = photo.Months.FirstOrDefault(item => item.Month == monthStart);
        return (
            new VacationInput(
                vacation.Id.Value, vacation.Origin, start, end, businessDays, PaidHere: false,
                photo.SalaryAtStart, photo.SalaryRecordDate, photo.PreviousMonthIbc, photo.PreviousIbcSource, null,
                month is null ? VacationAdvance.Zero : new VacationAdvance(month.Health, month.Pension, month.SolidarityFund)),
            null,
            photoOverrides.ContainsKey(vacation.Id.Value) ? null : paying.Value.Start);
    }

    // «Pagada» (spec rule 5): the paying quincena is Liquidated, Approved or Closed and not Outdated, and its result of the
    // employee carries the photo. An override is the photo the paying quincena is about to write, so it counts as paid.
    private async Task<VacationPhoto?> PhotoAsync(
        PayrollPeriod period,
        HrEmployeeSource employee,
        Vacation vacation,
        DateOnly payingStart,
        IReadOnlyDictionary<Guid, VacationPhoto> photoOverrides,
        CancellationToken cancellationToken)
    {
        if (photoOverrides.TryGetValue(vacation.Id.Value, out var overridden))
        {
            return overridden;
        }

        var paying = await periods.FindByStartAsync(period.TenantId, period.CompanyId, payingStart, cancellationToken);
        if (paying is not { Outdated: false, Status: PayrollPeriodStatus.Liquidated or PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed })
        {
            return null;
        }

        var stored = await results.FindAsync(period.TenantId, paying.Id, employee.EmployeeId, cancellationToken);
        return stored?.Liquidation.VacationPhotos.FirstOrDefault(photo => photo.VacationId == vacation.Id.Value);
    }

    // Spec «IBC anterior al inicio»: the month before the month of the start, from QEP if both of its halves have a result of the
    // employee; else the typed value; else the salary of the day before the start.
    private async Task<(decimal Ibc, VacationIbcSource Source)> PreviousIbcAsync(
        PayrollPeriod period,
        HrEmployeeSource employee,
        HrContractSource contract,
        Vacation vacation,
        CancellationToken cancellationToken)
    {
        var previousMonth = new DateOnly(vacation.StartDate.Year, vacation.StartDate.Month, 1).AddMonths(-1);
        var first = await periods.FindByStartAsync(period.TenantId, period.CompanyId, previousMonth, cancellationToken);
        var second = await periods.FindByStartAsync(
            period.TenantId, period.CompanyId, new DateOnly(previousMonth.Year, previousMonth.Month, 16), cancellationToken);
        // «Liquidada o más»: Reopen and MarkOutdated keep the old result rows, so a stored result alone proves nothing.
        if (IsSettled(first) && IsSettled(second))
        {
            var ofFirst = await results.FindAsync(period.TenantId, first.Id, employee.EmployeeId, cancellationToken);
            var ofSecond = await results.FindAsync(period.TenantId, second.Id, employee.EmployeeId, cancellationToken);
            var ibc = ofSecond?.Liquidation.Lines.FirstOrDefault(line => line.Code == IbcLineCode);
            if (ofFirst is not null && ibc is not null)
            {
                return (ibc.Amount, VacationIbcSource.Qep);
            }
        }

        if (vacation.PreviousMonthIbc is { } typed)
        {
            return (typed, VacationIbcSource.Typed);
        }

        return (SalaryOn(contract, vacation.StartDate.AddDays(-1)).Amount, VacationIbcSource.Salary);
    }

    private static bool IsSettled([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] PayrollPeriod? period) =>
        period is { Outdated: false, Status: PayrollPeriodStatus.Liquidated or PayrollPeriodStatus.Approved or PayrollPeriodStatus.Closed };

    // The salary record in force on the day; a day before the first record takes the first one (Hr guarantees one from the start).
    private static HrSalarySource SalaryOn(HrContractSource contract, DateOnly day)
    {
        var ordered = contract.Salaries.OrderBy(salary => salary.EffectiveDate).ToList();
        return ordered.LastOrDefault(salary => salary.EffectiveDate <= day) ?? ordered.First();
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
