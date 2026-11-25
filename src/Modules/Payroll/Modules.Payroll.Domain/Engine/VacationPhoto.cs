namespace Modules.Payroll.Domain.Engine;

/// <summary>What the paying quincena retained for one month of a vacation (the part of the IBC and the three advances).</summary>
public sealed record VacationMonthAdvance(DateOnly Month, decimal IbcPart, decimal Health, decimal Pension, decimal SolidarityFund);

/// <summary>What the paying quincena froze for the following ones (spec «Foto de las vacaciones»); Months = retained, not computed.</summary>
public sealed record VacationPhoto(
    Guid VacationId,
    decimal SalaryAtStart,
    DateOnly SalaryRecordDate,
    decimal PreviousMonthIbc,
    VacationIbcSource PreviousIbcSource,
    IReadOnlyList<VacationQuincenaDays> Quincenas,
    IReadOnlyList<VacationMonthAdvance> Months);
