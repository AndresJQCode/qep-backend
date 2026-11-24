using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Golden;

public sealed class GoldenCaseTests
{
    public static TheoryData<string> CaseNames()
    {
        var names = new TheoryData<string>();
        foreach (var golden in GoldenCaseLoader.All())
        {
            names.Add(golden.Name);
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryCaseMatches(string name)
    {
        var golden = GoldenCaseLoader.Load(name);
        var input = golden.Input.ToEngineInput(GoldenCaseLoader.LoadLegal(golden.Legal));

        var result = PayrollEngine.Liquidate(input);

        var differences = new List<string>();
        void Compare(string what, object expected, object actual)
        {
            if (!Equals(expected, actual))
            {
                differences.Add($"{what}: esperado {expected}, obtenido {actual}");
            }
        }

        Compare("workedDays", golden.Expected.WorkedDays, result.WorkedDays);
        Compare("finalSettlementPending", golden.Expected.FinalSettlementPending, result.FinalSettlementPending);
        Compare("negativeNet", golden.Expected.NegativeNet, result.NegativeNet);
        if (golden.Expected.VacationDays is { } vacationDays)
        {
            Compare("vacationDays", vacationDays, result.VacationDays);
        }

        Compare("net", golden.Expected.Net, result.Summary.Net);
        Compare("employerCost", golden.Expected.EmployerCost, result.Summary.EmployerCost);

        var expectedLines = golden.Expected.Lines;
        for (var index = 0; index < Math.Max(expectedLines.Count, result.Lines.Count); index++)
        {
            var expected = index < expectedLines.Count ? $"{expectedLines[index].Code} = {expectedLines[index].Amount}" : "(sin línea)";
            var actual = index < result.Lines.Count ? $"{result.Lines[index].Code} = {result.Lines[index].Amount}" : "(sin línea)";
            Compare($"línea {index + 1}", expected, actual);
        }

        Assert.True(differences.Count == 0, $"{name}:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
    }

    [Fact]
    public void EveryRuleHasACase()
    {
        var rules = typeof(PayrollEngine).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(PayrollEngine).Namespace && type.IsAbstract && type.IsSealed)
            .Select(type => type.Name)
            .Where(name => name.EndsWith("Rule", StringComparison.Ordinal) || name is "OvertimeFactors" or "PayrollEngine")
            .ToHashSet(StringComparer.Ordinal);
        var covered = GoldenCaseLoader.All().SelectMany(golden => golden.Covers).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(rules);
        Assert.Empty(rules.Except(covered));
        Assert.Empty(covered.Except(rules));
    }

    [Fact]
    public void ThereAreFortySyntheticFilesWithUniqueNames()
    {
        var cases = GoldenCaseLoader.All();

        // 29 from the first project and 11 of enjoyed vacations.
        Assert.Equal(40, cases.Count(golden => golden.Source == "synthetic"));
        Assert.Equal(cases.Count, cases.Select(golden => golden.Name).Distinct().Count());
    }
}
