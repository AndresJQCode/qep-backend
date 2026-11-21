namespace Modules.Hr.Domain;

/// <summary>Los topes del término fijo (Ley 2466/2025) y el aviso de vencimiento.</summary>
public static class FixedTermLimits
{
    public const int MaxYears = 4;

    public const int ShortRenewalsBeforeMinimumYear = 3;

    public const int NoticeDays = 30;

    public static DateOnly MaxEndDate(DateOnly startDate) => startDate.AddYears(MaxYears).AddDays(-1);

    public static bool IsShort(DateOnly previousEndDate, DateOnly newEndDate) => newEndDate < previousEndDate.AddYears(1);

    /// <summary>
    /// Incluye los que ya vencieron sin terminación registrada: en la fase 1 nada los desactiva solo, y dejar de avisar justo
    /// cuando venció sería peor.
    /// </summary>
    public static bool NoticeDue(EmploymentContract contract, DateOnly today) =>
        contract is { Duration: ContractDuration.FixedTerm, Status: ContractStatus.Active, EndDate: { } end }
        && end <= today.AddDays(NoticeDays);

    public static int? DaysUntilEnd(EmploymentContract contract, DateOnly today) =>
        contract is { Status: ContractStatus.Active, EndDate: { } end } ? end.DayNumber - today.DayNumber : null;
}
