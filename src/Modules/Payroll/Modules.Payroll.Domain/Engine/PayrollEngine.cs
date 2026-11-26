using System.Globalization;

namespace Modules.Payroll.Domain.Engine;

/// <summary>
/// El motor puro: una quincena de un empleado entra, su liquidación sale. Sin base de datos, sin reloj y sin servicios; la misma
/// entrada da siempre la misma salida. Cualquier cambio de regla sube <see cref="Version"/> (R-26).
/// </summary>
public static class PayrollEngine
{
    public const string Version = "2026.11.5";

    public static EmployeeLiquidation Liquidate(EngineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var prior = input.Prior;

        var worked = WorkedDaysRule.Compute(input.Period, input.ContractStart, input.ContractEndedOn);
        var vacations = VacationDaysRule.Compute(input, worked);
        var salary = SalaryRule.Compute(input, worked, vacations);
        var incomes = IncomeRule.Compute(input);
        var recurring = RecurringConceptRule.Compute(input, worked, vacations);
        var salaryIncome = incomes.SalaryIncome + recurring.SalaryIncome;
        var nonSalaryIncome = incomes.NonSalaryIncome + recurring.NonSalaryIncome;

        // Overtime is computed before the month income because the month adds it; its base only needs the salary income.
        var overtime = OvertimeRule.Compute(input, salary.SalaryInForce, prior.SalaryIncome + salaryIncome);
        var month = MonthIncomeRule.Compute(input, salary.SalaryInForce, salaryIncome, overtime.Total, nonSalaryIncome);
        var transport = TransportAllowanceRule.Compute(input, worked, salary, month, vacations);
        var vacationPay = VacationPayRule.Compute(input);

        var ibc = IbcRule.Compute(input, worked, vacations, salary.Line.Amount, month);
        var workerLines = new List<PayrollLine>(EmployeeDeductionsRule.Compute(input, ibc, vacations));
        if (SolidarityFundRule.Compute(input, salary.SalaryInForce, ibc, vacations) is { } solidarity)
        {
            workerLines.Add(solidarity);
        }

        var otherDeductions = DeductionRule.Compute(input);
        var employer = EmployerContributionsRule.Compute(input, ibc, month);
        var provisions = ProvisionsRule.Compute(
            input, salary.Line.Amount, transport.Line.Amount, overtime.Total, salaryIncome, vacations.ServiceValue);

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

        // The advance can only withhold what the net leaves, so it is computed after every other deduction.
        var earned = lines.Where(line => line.Group == PayrollLineGroup.Earning).Sum(line => line.Amount);
        var capped = CapWithoutEarnings(workerLines, lines, input.Period.Half, vacations.Days);
        var deducted = lines.Where(line => line.Group == PayrollLineGroup.Deduction).Sum(line => line.Amount)
            + capped.Sum(line => line.Amount)
            + otherDeductions.Sum(line => line.Amount);
        var advance = VacationAdvanceRule.Compute(input, Math.Max(0m, earned - deducted));

        lines.AddRange(capped);
        lines.AddRange(advance.Lines);
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
            // Only the regular lines: an advance is for the days of another quincena or month, not part of this month's withholding.
            prior.HealthEmployee + Of(lines, "health_employee"),
            prior.PensionEmployee + Of(lines, "pension_employee"),
            prior.SolidarityFund + Of(lines, "solidarity_fund"),
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
            advance.Photos);
    }

    private static decimal Of(IEnumerable<PayrollLine> lines, string code) =>
        lines.Where(line => line.Code == code).Sum(line => line.Amount);

    // Spec «Tope por neto en la posterior»: a quincena with vacation days and no earning at all cannot leave a negative net, so
    // the worker's social security lines go to 0. The first half leaves it to the second; the second half does not withhold it.
    private static IReadOnlyList<PayrollLine> CapWithoutEarnings(
        IReadOnlyList<PayrollLine> workerLines,
        IReadOnlyList<PayrollLine> lines,
        PayrollHalf half,
        int vacationDays)
    {
        if (vacationDays == 0 || lines.Any(line => line.Group == PayrollLineGroup.Earning && line.Amount != 0m))
        {
            return workerLines;
        }

        return workerLines.Select(line =>
        {
            if (line.Amount <= 0m)
            {
                return line;
            }

            var note = half == PayrollHalf.First
                ? "Sin devengo en esta quincena: lo que falta se retiene en la segunda quincena del mes"
                : $"Sin devengo en esta quincena: {line.Amount.ToString("F0", CultureInfo.InvariantCulture)} pesos que no alcanzan a descontarse en esta quincena quedan sin retener";
            return line with
            {
                Amount = 0m,
                Trace = line.Trace with { Note = line.Trace.Note is null ? note : $"{line.Trace.Note}; {note}" },
            };
        }).ToList();
    }
}
