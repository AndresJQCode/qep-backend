namespace Modules.Payroll.Domain.Engine;

/// <summary>Los acumulados del mes que usan la exoneración, el tope del auxilio y el IBC (paso 4).</summary>
public sealed record MonthIncome(
    decimal SalaryIncomeMonth,
    decimal OvertimeMonth,
    decimal NonSalaryIncomeMonth,
    decimal MonthSalaryIncome,
    decimal TransportCapIncome);

/// <summary>Paso 4: lo de la primera quincena más lo de ésta. No redondea: suma montos ya redondeados.</summary>
public static class MonthIncomeRule
{
    public static MonthIncome Compute(
        EngineInput input,
        decimal salaryInForce,
        decimal salaryIncome,
        decimal overtime,
        decimal nonSalaryIncome)
    {
        var salaryIncomeMonth = input.Prior.SalaryIncome + salaryIncome;
        var overtimeMonth = input.Prior.Overtime + overtime;
        var nonSalaryIncomeMonth = input.Prior.NonSalaryIncome + nonSalaryIncome;

        // SalaryAndPaidAbsences counts only the salary until absences exist (R-22).
        var transportCapIncome = input.Settings.TransportCapBase == TransportCapBase.SalaryAndSalaryIncome
            ? salaryInForce + salaryIncomeMonth
            : salaryInForce;

        return new MonthIncome(
            salaryIncomeMonth,
            overtimeMonth,
            nonSalaryIncomeMonth,
            salaryInForce + salaryIncomeMonth + overtimeMonth,
            transportCapIncome);
    }
}
