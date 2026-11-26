namespace Modules.Payroll.Domain.Engine;

/// <summary>Where the IBC of the month before the vacation came from: QEP's own result, a typed value, or the salary as a fallback.</summary>
public enum VacationIbcSource
{
    Qep,
    Typed,
    Salary,
}

/// <summary>What was withheld in advance from the worker for the vacation days of one month (term (b) of the spec's algebra).</summary>
public sealed record VacationAdvance(decimal Health, decimal Pension, decimal SolidarityFund)
{
    public static VacationAdvance Zero { get; } = new(0m, 0m, 0m);
}

/// <summary>One vacation as the engine reads it for one quincena.</summary>
/// <param name="PaidHere">This quincena contains StartDate and the origin is Qep: it pays the whole vacation.</param>
/// <param name="MonthVacationPay">External only: what Siigo paid for the days of this period's month; null if not typed.</param>
/// <param name="MonthAdvance">Term (b) for this period's month; Zero when PaidHere.</param>
public sealed record VacationInput(
    Guid VacationId,
    VacationOrigin Origin,
    DateOnly StartDate,
    DateOnly EndDate,
    int BusinessDays,
    bool PaidHere,
    decimal SalaryAtStart,
    DateOnly SalaryRecordDate,
    decimal PreviousMonthIbc,
    VacationIbcSource PreviousIbcSource,
    decimal? MonthVacationPay,
    VacationAdvance MonthAdvance);
