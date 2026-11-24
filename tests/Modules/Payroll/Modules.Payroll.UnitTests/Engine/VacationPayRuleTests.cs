using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Engine;

public sealed class VacationPayRuleTests
{
    private static readonly DateOnly Start = new(2026, 12, 14);
    private static readonly DateOnly End = new(2027, 1, 5);

    private static EngineInput InputWith(params VacationInput[] vacations) =>
        EngineTestData.Input(new DateOnly(2026, 12, 1), 3_000_000m) with { Vacations = vacations };

    [Fact]
    public void PaysEveryPayrollDayOfTheRange()
    {
        var vacation = EngineTestData.Vacation(Start, End, paidHere: true);

        var outcome = VacationPayRule.Compute(InputWith(vacation));

        var line = Assert.Single(outcome.Lines);
        Assert.Equal($"vacation_pay:{vacation.VacationId}", line.Code);
        Assert.Equal(PayrollLineGroup.Earning, line.Group);
        Assert.Equal("Vacaciones disfrutadas", line.Label);
        Assert.Equal(2_200_000m, line.Amount);
        Assert.Equal(2_200_000m, outcome.Total);
        Assert.Null(line.SalaryConstitutive);

        var inputs = line.Trace.Inputs.ToDictionary(item => item.Name, item => item.Value);
        Assert.Equal(Start, inputs["Inicio"]);
        Assert.Equal(End, inputs["Fin"]);
        Assert.Equal(15, inputs["Días hábiles"]);
        Assert.Equal(2, inputs["Quincena 2026-12-01"]);
        Assert.Equal(15, inputs["Quincena 2026-12-16"]);
        Assert.Equal(5, inputs["Quincena 2027-01-01"]);
        Assert.Equal(3_000_000m, inputs["Salario del inicio"]);
        Assert.Equal(new DateOnly(2025, 1, 1), inputs["Registro del salario del inicio"]);
        Assert.Equal("CST arts. 186 y 192; pago anticipado de todo el disfrute", line.Trace.Note);
    }

    [Fact]
    public void OnlyTheQuincenaThatPays()
    {
        var notHere = EngineTestData.Vacation(Start, End, paidHere: false);
        var external = EngineTestData.Vacation(Start, End, paidHere: false, origin: VacationOrigin.External, monthPay: 100_000m);

        var outcome = VacationPayRule.Compute(InputWith(notHere, external));

        Assert.Empty(outcome.Lines);
        Assert.Equal(0m, outcome.Total);
    }

    [Fact]
    public void TwoVacationsTwoLines()
    {
        var first = EngineTestData.Vacation(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3), paidHere: true);
        var second = EngineTestData.Vacation(new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 15), paidHere: true);

        var outcome = VacationPayRule.Compute(InputWith(first, second));

        Assert.Equal([$"vacation_pay:{first.VacationId}", $"vacation_pay:{second.VacationId}"], outcome.Lines.Select(line => line.Code));
        Assert.Equal([300_000m, 200_000m], outcome.Lines.Select(line => line.Amount));
        Assert.Equal(500_000m, outcome.Total);
    }
}
