using System.Diagnostics;
using System.Text.Json;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class PayrollEngineTests
{
    private static decimal Amount(EmployeeLiquidation result, string code) =>
        result.Lines.Single(line => line.Code == code).Amount;

    [Fact]
    public void TheMinimumWageFirstHalfMatchesTheAnchor()
    {
        var result = PayrollEngine.Liquidate(EngineTestData.Input(new DateOnly(2026, 11, 1)));

        Assert.Equal(
            [
                ("salary", 875_453m),
                ("transport_allowance", 124_548m),
                ("ibc", 875_453m),
                ("health_employee", 35_018m),
                ("pension_employee", 35_018m),
                ("health_employer", 0m),
                ("pension_employer", 105_100m),
                ("arl", 4_600m),
                ("compensation_fund", 35_100m),
                ("icbf", 0m),
                ("sena", 0m),
                ("severance", 83_300m),
                ("severance_interest", 10_000m),
                ("premium", 83_300m),
                ("vacation", 36_506m),
            ],
            result.Lines.Select(line => (line.Code, line.Amount)));
        Assert.Equal(15, result.WorkedDays);
        Assert.False(result.FinalSettlementPending);
        Assert.False(result.NegativeNet);
        Assert.Equal(929_965m, result.Summary.Net);
        Assert.Equal(1_357_907m, result.Summary.EmployerCost);
        Assert.Equal("2026.11.5", result.EngineVersion);
        Assert.Equal(PayrollEngine.Version, result.EngineVersion);
    }

    [Fact]
    public void EngineVersionIs2026Dot11Dot5() => Assert.Equal("2026.11.5", PayrollEngine.Version);

    [Fact]
    public void WorkedDaysExcludeVacationDays()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 12, 16), 3_000_000m) with
        {
            Prior = MonthAccumulators.Empty with { WorkedDays = 13 },
            Vacations = [EngineTestData.Vacation(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), paidHere: false)],
        };

        var result = PayrollEngine.Liquidate(input);

        Assert.Equal(0, result.WorkedDays);
        Assert.Equal(15, result.VacationDays);
        Assert.Equal(13, result.Month.WorkedDays);
        Assert.Equal(0m, Amount(result, "salary"));
    }

    [Fact]
    public void VacationPayIsEarnedButNotSalary()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 12, 1), 3_000_000m) with
        {
            Vacations = [EngineTestData.Vacation(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), paidHere: true)],
        };

        var result = PayrollEngine.Liquidate(input);

        Assert.Equal(["salary", "vacation_pay"], result.Lines.Take(2).Select(line => line.Code.Split(':')[0]));
        Assert.Equal(2_200_000m, result.Summary.VacationPay);
        Assert.Equal(1_300_000m, result.Summary.Salary);
        Assert.Equal(
            result.Summary.Salary + result.Summary.VacationPay + result.Summary.TransportAllowance,
            result.Summary.TotalEarned);
        // The pay is neither month income nor part of the allowance cap: the allowance stays what 13 worked days earn.
        Assert.Equal(107_941m, result.Summary.TransportAllowance);
        Assert.Equal(2_200_000m, PayrollSummary.Sum([result.Summary]).VacationPay);
    }

    // Review focus 3 and spec «Tope por neto en la posterior»: no earning, so the worker lines never go below a net of 0.
    [Fact]
    public void NoEarningsCapsTheWorkerLines()
    {
        var second = EngineTestData.Input(new DateOnly(2026, 12, 16), 3_000_000m) with
        {
            Prior = MonthAccumulators.Empty with { WorkedDays = 15, SalaryRaw = 1_500_000m, Salary = 1_500_000m },
            Vacations =
            [
                EngineTestData.Vacation(
                    new DateOnly(2026, 12, 16), new DateOnly(2027, 1, 5), paidHere: false,
                    previousIbc: 1_750_905m, advance: new VacationAdvance(89_054m, 89_054m, 0m)),
            ],
        };
        var first = EngineTestData.Input(new DateOnly(2026, 12, 1), 3_000_000m) with
        {
            Vacations =
            [
                EngineTestData.Vacation(new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 20), paidHere: false, previousIbc: 1_750_905m),
            ],
        };

        var secondResult = PayrollEngine.Liquidate(second);
        var firstResult = PayrollEngine.Liquidate(first);

        foreach (var code in new[] { "health_employee", "pension_employee" })
        {
            var line = secondResult.Lines.Single(item => item.Code == code);
            Assert.Equal(0m, line.Amount);
            Assert.Equal("Sin devengo en esta quincena: 5964 pesos que no alcanzan a descontarse en esta quincena quedan sin retener", line.Trace.Note);

            var early = firstResult.Lines.Single(item => item.Code == code);
            Assert.Equal(0m, early.Amount);
            Assert.Equal("Sin devengo en esta quincena: lo que falta se retiene en la segunda quincena del mes", early.Trace.Note);
        }

        Assert.Equal(0m, secondResult.Summary.Net);
        Assert.Equal(0m, firstResult.Summary.Net);
        Assert.Equal(0m, secondResult.Month.HealthEmployee);
    }

    [Fact]
    public void AdvanceLinesStayOutOfTheMonthAccumulators()
    {
        var input = EngineTestData.Input(new DateOnly(2027, 1, 1), 3_000_000m) with
        {
            Vacations = [EngineTestData.Vacation(new DateOnly(2027, 1, 12), new DateOnly(2027, 2, 1), paidHere: true)],
        };

        var result = PayrollEngine.Liquidate(input);

        Assert.Equal(
            ["health_employee", "pension_employee", "health_employee_vacation_advance", "pension_employee_vacation_advance"],
            result.Lines.Where(line => line.Group == PayrollLineGroup.Deduction).Select(line => line.Code));
        Assert.Equal(60_000m, result.Month.HealthEmployee);
        Assert.Equal(60_000m, result.Month.PensionEmployee);
        Assert.Equal(124_000m, result.Summary.HealthEmployee);
        Assert.Equal(124_000m, result.Summary.PensionEmployee);
        Assert.Equal(248_000m, result.Summary.TotalDeductions);
        var photo = Assert.Single(result.VacationPhotos);
        Assert.Equal(input.Vacations[0].VacationId, photo.VacationId);
    }

    [Fact]
    public void TheSummaryIsDerivedFromTheLines()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 8, 1), 2_100_000m) with
        {
            Overtime = [EngineTestData.Overtime(SystemConceptKey.ExtraDay, 10m)],
            Incomes =
            [
                EngineTestData.Income(SystemConceptKey.Commissions, 100_000m, true),
                EngineTestData.Income(SystemConceptKey.OtherIncome, 50_000m, false),
            ],
            Deductions = [new DeductionInput(SystemConcepts.All[^1].Id, "Otra deducción", 30_000m)],
        };

        var summary = PayrollEngine.Liquidate(input).Summary;

        Assert.Equal(1_050_000m, summary.Salary);
        Assert.Equal(125_000m, summary.Overtime);
        Assert.Equal(100_000m, summary.OtherSalaryIncome);
        Assert.Equal(50_000m, summary.NonSalaryIncome);
        Assert.Equal(summary.Salary + summary.TransportAllowance + 125_000m + 100_000m + 50_000m, summary.TotalEarned);
        Assert.Equal(30_000m, summary.OtherDeductions);
        Assert.Equal(summary.HealthEmployee + summary.PensionEmployee + summary.SolidarityFund + 30_000m, summary.TotalDeductions);
        Assert.Equal(summary.TotalEarned - summary.TotalDeductions, summary.Net);
    }

    [Fact]
    public void TheSummariesAddUp()
    {
        var first = PayrollEngine.Liquidate(EngineTestData.Input(new DateOnly(2026, 11, 1))).Summary;

        var total = PayrollSummary.Sum([first, first]);

        Assert.Equal(first.Net * 2, total.Net);
        Assert.Equal(first.EmployerCost * 2, total.EmployerCost);
        Assert.Equal(first.Vacation * 2, total.Vacation);
    }

    [Fact]
    public void ANegativeNetIsFlagged()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 1)) with
        {
            Deductions = [new DeductionInput(SystemConcepts.All[^1].Id, "Otra deducción", 2_000_000m)],
        };

        var result = PayrollEngine.Liquidate(input);

        Assert.True(result.NegativeNet);
        Assert.True(result.Summary.Net < 0m);
        Assert.Equal(2_000_000m, Amount(result, $"deduction:{SystemConcepts.All[^1].Id}:0"));
    }

    [Fact]
    public void LinesComeInContractOrder()
    {
        var recurringIncome = new RecurringInput(Guid.NewGuid(), "Bono", ConceptKind.Income, RecurringMode.Fixed, 50_000m, true, null);
        var recurringDeduction = new RecurringInput(Guid.NewGuid(), "Libranza", ConceptKind.Deduction, RecurringMode.Fixed, 20_000m, false, null);
        var input = EngineTestData.Input(new DateOnly(2026, 8, 1), 8_000_000m) with
        {
            Overtime =
            [
                EngineTestData.Overtime(SystemConceptKey.ExtraNight, 2m),
                EngineTestData.Overtime(SystemConceptKey.ExtraDay, 2m),
            ],
            Incomes = [EngineTestData.Income(SystemConceptKey.Commissions, 100_000m, true)],
            Deductions = [new DeductionInput(SystemConcepts.All[^1].Id, "Otra deducción", 10_000m)],
            Recurring = [recurringIncome, recurringDeduction],
        };

        var codes = PayrollEngine.Liquidate(input).Lines.Select(line => line.Code).ToArray();

        Assert.Equal(
            [
                "salary",
                "transport_allowance",
                $"overtime:{SystemConcepts.All[1].Id}",
                $"overtime:{SystemConcepts.All[0].Id}",
                $"income:{SystemConcepts.All[7].Id}:0",
                $"recurring:{recurringIncome.RecurringId}",
                "ibc",
                $"recurring:{recurringDeduction.RecurringId}",
                "health_employee",
                "pension_employee",
                "solidarity_fund",
                $"deduction:{SystemConcepts.All[^1].Id}:0",
                "health_employer",
                "pension_employer",
                "arl",
                "compensation_fund",
                "icbf",
                "sena",
                "severance",
                "severance_interest",
                "premium",
                "vacation",
            ],
            codes);
    }

    [Fact]
    public void TheSameInputGivesTheSameOutput()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 8, 1), 3_000_000m) with
        {
            Overtime = [EngineTestData.Overtime(SystemConceptKey.ExtraSundayNight, 3.5m)],
            Incomes = [EngineTestData.Income(SystemConceptKey.OtherIncome, 250_000m, false)],
        };

        var first = JsonSerializer.Serialize(PayrollEngine.Liquidate(input));
        var second = JsonSerializer.Serialize(PayrollEngine.Liquidate(input));

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheMonthAccumulatorsFeedTheSecondHalf()
    {
        var first = PayrollEngine.Liquidate(EngineTestData.Input(new DateOnly(2026, 11, 1)));

        var second = PayrollEngine.Liquidate(EngineTestData.Input(new DateOnly(2026, 11, 16)) with { Prior = first.Month });

        Assert.Equal(
            [
                ("salary", 875_452m),
                ("transport_allowance", 124_547m),
                ("ibc", 1_750_905m),
                ("health_employee", 35_018m),
                ("pension_employee", 35_018m),
                ("health_employer", 0m),
                ("pension_employer", 105_100m),
                ("arl", 4_600m),
                ("compensation_fund", 35_000m),
                ("icbf", 0m),
                ("sena", 0m),
                ("severance", 83_300m),
                ("severance_interest", 10_000m),
                ("premium", 83_300m),
                ("vacation", 36_506m),
            ],
            second.Lines.Select(line => (line.Code, line.Amount)));
        Assert.Equal(30, second.Month.WorkedDays);
        Assert.Equal(1_750_905m, second.Month.Salary);
    }

    [Fact]
    public void AnEndInsideTheSecondHalfIsPendingFinalSettlement()
    {
        var input = EngineTestData.Input(new DateOnly(2026, 11, 16)) with
        {
            ContractEndedOn = new DateOnly(2026, 11, 20),
            Prior = PayrollEngine.Liquidate(EngineTestData.Input(new DateOnly(2026, 11, 1))).Month,
        };

        var result = PayrollEngine.Liquidate(input);

        Assert.Equal(5, result.WorkedDays);
        Assert.True(result.FinalSettlementPending);
    }

    [Fact]
    public void FiveHundredEmployeesUnderTenSeconds()
    {
        var inputs = Enumerable.Range(0, 500).Select(index =>
        {
            var salary = 1_750_905m + (index * 37_000m);
            var day = index % 2 == 0 ? new DateOnly(2026, 8, 1) : new DateOnly(2026, 8, 16);
            var input = EngineTestData.Input(day, salary) with
            {
                Overtime = [EngineTestData.Overtime(SystemConceptKey.ExtraSundayDay, 1 + (index % 8))],
                Incomes = [EngineTestData.Income(SystemConceptKey.Commissions, 10_000m * (index % 5), index % 3 == 0)],
            };
            return day.Day == 16
                ? input with
                {
                    Prior = MonthAccumulators.Empty with { WorkedDays = 15, SalaryRaw = salary / 2, Salary = Math.Round(salary / 2) },
                }
                : input;
        }).ToList();

        var watch = Stopwatch.StartNew();
        var results = inputs.Select(PayrollEngine.Liquidate).ToList();
        watch.Stop();

        Assert.Equal(500, results.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
