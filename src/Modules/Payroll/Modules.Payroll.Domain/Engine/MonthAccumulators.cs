namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// Lo acumulado del mes hasta una quincena (R-5). <c>SalaryRaw</c> y <c>TransportRaw</c> van sin redondear; <c>TransportRaw</c> es lo
/// causado por días, se pague o no (decisión del owner 2). No guarda el IBC: la
/// segunda quincena lo recalcula del mes (M-7).
/// </summary>
public sealed record MonthAccumulators(
    int WorkedDays,
    decimal SalaryRaw,
    decimal Salary,
    decimal TransportRaw,
    decimal Transport,
    decimal Overtime,
    decimal SalaryIncome,
    decimal NonSalaryIncome,
    decimal HealthEmployee,
    decimal PensionEmployee,
    decimal SolidarityFund,
    decimal HealthEmployer,
    decimal PensionEmployer,
    decimal Arl,
    decimal CompensationFund,
    decimal Icbf,
    decimal Sena)
{
    public static MonthAccumulators Empty { get; } = new(0, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}
