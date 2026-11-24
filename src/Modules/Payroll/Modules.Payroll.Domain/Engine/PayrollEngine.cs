namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// El motor puro: una quincena de un empleado entra, su liquidación sale. Sin base de datos, sin reloj y sin servicios; la misma
/// entrada da siempre la misma salida. Cualquier cambio de regla sube <see cref="Version"/> (R-26).
/// </summary>
public static class PayrollEngine
{
    public const string Version = "2026.11.4";

    public static EmployeeLiquidation Liquidate(EngineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var prior = input.Prior;

        var worked = WorkedDaysRule.Compute(input.Period, input.ContractStart, input.ContractEndedOn);
        var vacations = VacationDaysRule.Compute(input, worked);
        var salary = SalaryRule.Compute(input, worked, vacations);
        var incomes = IncomeRule.Compute(input);
        var recurring = RecurringConceptRule.Compute(input, worked);
        var salaryIncome = incomes.SalaryIncome + recurring.SalaryIncome;
        var nonSalaryIncome = incomes.NonSalaryIncome + recurring.NonSalaryIncome;

        // Overtime is computed before the month income because the month adds it; its base only needs the salary income.
        var overtime = OvertimeRule.Compute(input, salary.SalaryInForce, prior.SalaryIncome + salaryIncome);
        var month = MonthIncomeRule.Compute(input, salary.SalaryInForce, salaryIncome, overtime.Total, nonSalaryIncome);
        var transport = TransportAllowanceRule.Compute(input, worked, salary, month, vacations);
        var vacationPay = VacationPayRule.Compute(input);

        var ibc = IbcRule.Compute(input, worked, salary.Line.Amount, month);
        var employeeDeductions = EmployeeDeductionsRule.Compute(input, ibc);
        var solidarity = SolidarityFundRule.Compute(input, salary.SalaryInForce, ibc);
        var otherDeductions = DeductionRule.Compute(input);
        var employer = EmployerContributionsRule.Compute(input, ibc, month);
        var provisions = ProvisionsRule.Compute(input, salary.Line.Amount, transport.Line.Amount, overtime.Total, salaryIncome);

        // Fixed order (I-11): Earning, Info, Deduction, EmployerContribution, Provision; inside a group, the contract's code table
        // (a recurring deduction sits before health_employee there).
        var lines = new List<PayrollLine> { salary.Line };
        lines.AddRange(vacationPay.Lines);
        lines.Add(transport.Line);
        lines.AddRange(overtime.Lines);
        lines.AddRange(incomes.Lines);
        lines.AddRange(recurring.IncomeLines);
        lines.Add(ibc.Line);
        lines.AddRange(recurring.DeductionLines);
        lines.AddRange(employeeDeductions);
        if (solidarity is not null)
        {
            lines.Add(solidarity);
        }

        lines.AddRange(otherDeductions);
        lines.AddRange(employer);
        lines.AddRange(provisions);

        var summary = PayrollSummary.From(lines);
        var workedDays = worked.Days - vacations.Days;
        var accumulators = new MonthAccumulators(
            prior.WorkedDays + workedDays,
            prior.SalaryRaw + salary.Raw,
            prior.Salary + salary.Line.Amount,
            prior.TransportRaw + transport.Raw,
            prior.Transport + transport.Line.Amount,
            month.OvertimeMonth,
            month.SalaryIncomeMonth,
            month.NonSalaryIncomeMonth,
            prior.HealthEmployee + summary.HealthEmployee,
            prior.PensionEmployee + summary.PensionEmployee,
            prior.SolidarityFund + summary.SolidarityFund,
            prior.HealthEmployer + summary.HealthEmployer,
            prior.PensionEmployer + summary.PensionEmployer,
            prior.Arl + summary.Arl,
            prior.CompensationFund + summary.CompensationFund,
            prior.Icbf + summary.Icbf,
            prior.Sena + summary.Sena);

        return new EmployeeLiquidation(
            input.EmployeeId,
            workedDays,
            worked.FinalSettlementPending,
            summary.Net < 0m,
            lines,
            summary,
            accumulators,
            Version,
            vacations.Days,
            []);
    }
}
