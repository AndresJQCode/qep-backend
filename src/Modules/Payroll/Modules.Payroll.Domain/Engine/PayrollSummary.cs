namespace Modules.Payroll.Domain.Engine;

/// <summary>Los totales de una liquidación, con la misma forma que <c>PayrollTotalsDto</c>. Se derivan de las líneas, nunca se calculan aparte.</summary>
public sealed record PayrollSummary(
    decimal Salary,
    decimal VacationPay,
    decimal TransportAllowance,
    decimal Overtime,
    decimal OtherSalaryIncome,
    decimal NonSalaryIncome,
    decimal TotalEarned,
    decimal HealthEmployee,
    decimal PensionEmployee,
    decimal SolidarityFund,
    decimal OtherDeductions,
    decimal TotalDeductions,
    decimal Net,
    decimal HealthEmployer,
    decimal PensionEmployer,
    decimal Arl,
    decimal CompensationFund,
    decimal Icbf,
    decimal Sena,
    decimal Severance,
    decimal SeveranceInterest,
    decimal Premium,
    decimal Vacation,
    decimal EmployerCost)
{
    public static PayrollSummary From(IReadOnlyList<PayrollLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        decimal Of(string code) => lines.Where(line => line.Code == code).Sum(line => line.Amount);

        bool IsCaptured(PayrollLine line) =>
            line.Code.StartsWith("income:", StringComparison.Ordinal) || line.Code.StartsWith("recurring:", StringComparison.Ordinal);

        var earnings = lines.Where(line => line.Group == PayrollLineGroup.Earning).ToList();
        var salary = Of("salary");
        var vacationPay = earnings.Where(line => line.Code.StartsWith(VacationPayRule.CodePrefix, StringComparison.Ordinal)).Sum(line => line.Amount);
        var transport = Of("transport_allowance") + Of("connectivity_allowance");
        var overtime = earnings.Where(line => line.Code.StartsWith("overtime:", StringComparison.Ordinal)).Sum(line => line.Amount);
        var salaryIncome = earnings.Where(line => IsCaptured(line) && line.SalaryConstitutive == true).Sum(line => line.Amount);
        var nonSalaryIncome = earnings.Where(line => IsCaptured(line) && line.SalaryConstitutive != true).Sum(line => line.Amount);
        var totalEarned = salary + vacationPay + transport + overtime + salaryIncome + nonSalaryIncome;

        // What the colilla withholds: the regular lines plus the advance withheld for the vacation days of other quincenas.
        var health = Of("health_employee") + Of(VacationAdvanceRule.HealthCode);
        var pension = Of("pension_employee") + Of(VacationAdvanceRule.PensionCode);
        var fund = Of("solidarity_fund") + Of(VacationAdvanceRule.SolidarityFundCode);
        var otherDeductions = lines
            .Where(line => line.Group == PayrollLineGroup.Deduction && IsOther(line.Code))
            .Sum(line => line.Amount);
        var totalDeductions = health + pension + fund + otherDeductions;

        var healthEmployer = Of("health_employer");
        var pensionEmployer = Of("pension_employer");
        var arl = Of("arl");
        var compensationFund = Of("compensation_fund");
        var icbf = Of("icbf");
        var sena = Of("sena");
        var severance = Of("severance");
        var severanceInterest = Of("severance_interest");
        var premium = Of("premium");
        var vacation = Of("vacation");

        return new PayrollSummary(
            salary,
            vacationPay,
            transport,
            overtime,
            salaryIncome,
            nonSalaryIncome,
            totalEarned,
            health,
            pension,
            fund,
            otherDeductions,
            totalDeductions,
            totalEarned - totalDeductions,
            healthEmployer,
            pensionEmployer,
            arl,
            compensationFund,
            icbf,
            sena,
            severance,
            severanceInterest,
            premium,
            vacation,
            totalEarned + healthEmployer + pensionEmployer + arl + compensationFund + icbf + sena
                + severance + severanceInterest + premium + vacation);
    }

    public static PayrollSummary Sum(IEnumerable<PayrollSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        var all = summaries.ToList();
        return new PayrollSummary(
            all.Sum(item => item.Salary),
            all.Sum(item => item.VacationPay),
            all.Sum(item => item.TransportAllowance),
            all.Sum(item => item.Overtime),
            all.Sum(item => item.OtherSalaryIncome),
            all.Sum(item => item.NonSalaryIncome),
            all.Sum(item => item.TotalEarned),
            all.Sum(item => item.HealthEmployee),
            all.Sum(item => item.PensionEmployee),
            all.Sum(item => item.SolidarityFund),
            all.Sum(item => item.OtherDeductions),
            all.Sum(item => item.TotalDeductions),
            all.Sum(item => item.Net),
            all.Sum(item => item.HealthEmployer),
            all.Sum(item => item.PensionEmployer),
            all.Sum(item => item.Arl),
            all.Sum(item => item.CompensationFund),
            all.Sum(item => item.Icbf),
            all.Sum(item => item.Sena),
            all.Sum(item => item.Severance),
            all.Sum(item => item.SeveranceInterest),
            all.Sum(item => item.Premium),
            all.Sum(item => item.Vacation),
            all.Sum(item => item.EmployerCost));
    }

    private static bool IsOther(string code) =>
        code.StartsWith("deduction:", StringComparison.Ordinal) || code.StartsWith("recurring:", StringComparison.Ordinal);
}
