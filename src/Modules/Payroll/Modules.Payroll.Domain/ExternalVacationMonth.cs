namespace Modules.Payroll.Domain;

/// <summary>
/// What Siigo already did for one month of an external vacation, typed in by hand. <paramref name="Month"/> is the first day of the
/// month; an absent advance is 0 and an absent <paramref name="VacationPay"/> stays null (spec «Origen externo»).
/// </summary>
public sealed record ExternalVacationMonth(
    DateOnly Month,
    decimal? VacationPay,
    decimal HealthAdvance,
    decimal PensionAdvance,
    decimal SolidarityFundAdvance);
