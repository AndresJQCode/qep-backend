using Bootstrapper;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-21, «Valores legales»: el adaptador que arma el <c>LegalValueSet</c> de Payroll desde las tablas de Hr.</summary>
public sealed class PayrollLegalValuesTests
{
    private static readonly DateOnly Y2025 = new(2025, 1, 1);
    private static readonly DateOnly Y2026 = new(2026, 1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Los literales de la siembra viven sólo en la migración y en las pruebas.
    private static List<LegalParameterValue> SeededValues()
    {
        var rows = new List<LegalParameterValue>();

        void Yearly(LegalParameterKey key, decimal value2025, decimal? value2026 = null)
        {
            rows.Add(new(key, Y2025, value2025));
            rows.Add(new(key, Y2026, value2026 ?? value2025));
        }

        Yearly(LegalParameterKey.Smmlv, 1_423_500m, 1_750_905m);
        Yearly(LegalParameterKey.IntegralSalaryFloor, 18_505_500m, 22_761_765m);
        Yearly(LegalParameterKey.TransportAllowance, 200_000m, 249_095m);
        rows.Add(new(LegalParameterKey.ConnectivityAllowanceSince, new DateOnly(2025, 6, 25), 1m));
        Yearly(LegalParameterKey.HealthEmployeeRate, 0.04m);
        Yearly(LegalParameterKey.HealthEmployerRate, 0.085m);
        Yearly(LegalParameterKey.PensionEmployeeRate, 0.04m);
        Yearly(LegalParameterKey.PensionEmployerRate, 0.12m);
        Yearly(LegalParameterKey.HighRiskPensionTotalRate, 0.26m);
        Yearly(LegalParameterKey.ArlClassIRate, 0.00522m);
        Yearly(LegalParameterKey.ArlClassIIRate, 0.01044m);
        Yearly(LegalParameterKey.ArlClassIIIRate, 0.02436m);
        Yearly(LegalParameterKey.ArlClassIVRate, 0.0435m);
        Yearly(LegalParameterKey.ArlClassVRate, 0.0696m);
        Yearly(LegalParameterKey.CompensationFundRate, 0.04m);
        Yearly(LegalParameterKey.IcbfRate, 0.03m);
        Yearly(LegalParameterKey.SenaRate, 0.02m);
        Yearly(LegalParameterKey.SeveranceProvisionRate, 0.0833m);
        Yearly(LegalParameterKey.PremiumProvisionRate, 0.0833m);
        Yearly(LegalParameterKey.VacationProvisionRate, 0.0417m);
        Yearly(LegalParameterKey.SeveranceInterestProvisionRate, 0.01m);
        Yearly(LegalParameterKey.TransportAllowanceCapSmmlv, 2m);
        Yearly(LegalParameterKey.NonSalaryCapRate, 0.40m);
        Yearly(LegalParameterKey.ExonerationThresholdSmmlv, 10m);
        Yearly(LegalParameterKey.IbcCapSmmlv, 25m);
        rows.Add(new(LegalParameterKey.SundaySurchargeRate, new DateOnly(2025, 7, 1), 0.80m));
        rows.Add(new(LegalParameterKey.SundaySurchargeRate, new DateOnly(2026, 7, 1), 0.90m));
        rows.Add(new(LegalParameterKey.SundaySurchargeRate, new DateOnly(2027, 7, 1), 1.00m));
        rows.Add(new(LegalParameterKey.WeeklyHours, new DateOnly(2023, 7, 15), 47m));
        rows.Add(new(LegalParameterKey.WeeklyHours, new DateOnly(2024, 7, 15), 46m));
        rows.Add(new(LegalParameterKey.WeeklyHours, new DateOnly(2025, 7, 15), 44m));
        rows.Add(new(LegalParameterKey.WeeklyHours, new DateOnly(2026, 7, 15), 42m));
        Yearly(LegalParameterKey.ExtraDayRate, 0.25m);
        Yearly(LegalParameterKey.ExtraNightRate, 0.75m);
        Yearly(LegalParameterKey.NightSurchargeRate, 0.35m);
        return rows;
    }

    private static List<LegalParameterBracket> SeededBrackets()
    {
        var rows = new List<LegalParameterBracket>();
        foreach (var from in new[] { Y2025, Y2026 })
        {
            rows.Add(new(LegalBracketKey.SolidarityFund, from, 4m, 16m, 0.01m));
            rows.Add(new(LegalBracketKey.SolidarityFund, from, 16m, 17m, 0.012m));
            rows.Add(new(LegalBracketKey.SolidarityFund, from, 17m, 18m, 0.014m));
            rows.Add(new(LegalBracketKey.SolidarityFund, from, 18m, 19m, 0.016m));
            rows.Add(new(LegalBracketKey.SolidarityFund, from, 19m, 20m, 0.018m));
            rows.Add(new(LegalBracketKey.SolidarityFund, from, 20m, 25m, 0.02m));
        }

        return rows;
    }

    private sealed class FakeSource(
        IReadOnlyList<LegalParameterValue> values, IReadOnlyList<LegalParameterBracket> brackets) : ILegalParameterSource
    {
        public Task<IReadOnlyList<LegalParameterValue>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(values);

        public Task<IReadOnlyList<LegalParameterBracket>> ListBracketsAsync(CancellationToken cancellationToken) => Task.FromResult(brackets);
    }

    private static PayrollLegalValues Adapter(
        IReadOnlyList<LegalParameterValue>? values = null, IReadOnlyList<LegalParameterBracket>? brackets = null) =>
        new(new FakeSource(values ?? SeededValues(), brackets ?? SeededBrackets()));

    [Fact]
    public async Task ResolvesEveryKeyAtTheDate()
    {
        var set = await Adapter().ResolveAsync(new DateOnly(2026, 8, 1), Ct);

        Assert.Equal(new DateOnly(2026, 8, 1), set.ResolvedAt);
        Assert.Equal(0.90m, set.SundaySurchargeRate.Value);
        Assert.Equal(new DateOnly(2026, 7, 1), set.SundaySurchargeRate.EffectiveFrom);
        Assert.Equal(42m, set.WeeklyHours.Value);
        Assert.Equal(1_750_905m, set.Smmlv.Value);
        Assert.Equal(6, set.SolidarityBrackets.Count);
    }

    [Fact]
    public async Task TheFirstHalfOfJulyUsesThePreviousWeek()
    {
        var set = await Adapter().ResolveAsync(new DateOnly(2026, 7, 1), Ct);

        Assert.Equal(44m, set.WeeklyHours.Value);
    }

    [Fact]
    public async Task ConnectivityHasItsStartDate()
    {
        var set = await Adapter().ResolveAsync(new DateOnly(2026, 11, 1), Ct);

        Assert.Equal(new DateOnly(2025, 6, 25), set.ConnectivityAllowanceSince);
    }

    [Fact]
    public async Task AMissingKeyIsLegalParameterMissing()
    {
        var values = SeededValues().Where(value => value.Key != LegalParameterKey.SundaySurchargeRate).ToList();

        var exception = await Assert.ThrowsAsync<PayrollDomainException>(() =>
            Adapter(values).ResolveAsync(new DateOnly(2025, 3, 1), Ct));

        Assert.Equal("payroll.legal_parameter.missing", exception.Code);
        Assert.Contains("SundaySurchargeRate", exception.Message);
    }

    [Fact]
    public async Task NoBracketsIsLegalParameterMissing()
    {
        var exception = await Assert.ThrowsAsync<PayrollDomainException>(() =>
            Adapter(brackets: []).ResolveAsync(new DateOnly(2026, 3, 1), Ct));

        Assert.Equal("payroll.legal_parameter.missing", exception.Code);
        Assert.Contains("SolidarityFund", exception.Message);
    }

    [Theory]
    [InlineData("4", "0.01")]
    [InlineData("15.99", "0.01")]
    [InlineData("16", "0.012")]
    [InlineData("25", "0.02")]
    public async Task SolidarityRateIsLowerInclusive(string multiple, string expected)
    {
        var set = await Adapter().ResolveAsync(new DateOnly(2026, 3, 1), Ct);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            set.SolidarityRate(decimal.Parse(multiple, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task OutsideEveryBracketThereIsNoRate()
    {
        var set = await Adapter().ResolveAsync(new DateOnly(2026, 3, 1), Ct);

        Assert.Null(set.SolidarityRate(3.99m));
        Assert.Null(set.SolidarityRate(25.01m));
    }

    [Fact]
    public async Task TheSetSurvivesAJsonRoundTrip()
    {
        var set = await Adapter().ResolveAsync(new DateOnly(2026, 11, 1), Ct);

        var copy = System.Text.Json.JsonSerializer.Deserialize<LegalValueSet>(System.Text.Json.JsonSerializer.Serialize(set));

        Assert.Equal(set.ArlRate(ArlClass.V), copy!.ArlRate(ArlClass.V));
        Assert.Equal(set.SolidarityBrackets, copy.SolidarityBrackets);
        Assert.Equal(set.ConnectivityAllowanceSince, copy.ConnectivityAllowanceSince);
        Assert.Equal(set.WeeklyHours, copy.WeeklyHours);
        Assert.Equal(0.012m, copy.SolidarityRate(16m));
        Assert.Equal(0.02m, copy.SolidarityRate(25m));
        Assert.Null(copy.SolidarityRate(25.01m));
    }

    [Fact]
    public async Task AYearWithoutItsOwnRowsIsLegalParameterMissing()
    {
        var exception = await Assert.ThrowsAsync<PayrollDomainException>(() =>
            Adapter().ResolveAsync(new DateOnly(2027, 1, 14), Ct));

        Assert.Equal("payroll.legal_parameter.missing", exception.Code);
        Assert.Contains("Smmlv", exception.Message);
        Assert.Contains("TransportAllowance", exception.Message);
    }

    [Fact]
    public async Task TheMidYearKeysStillResolveAcrossYears()
    {
        // 2027-01-14: yearly keys have their 2027 row; the mid-year keys and connectivity do not.
        var values = SeededValues();
        foreach (var row in values.Where(value => value.EffectiveFrom == Y2026 && value.Key is not LegalParameterKey.IntegralSalaryFloor).ToList())
        {
            values.Add(new(row.Key, new DateOnly(2027, 1, 1), row.Value));
        }

        var brackets = SeededBrackets();
        brackets.AddRange(SeededBrackets()
            .Where(bracket => bracket.EffectiveFrom == Y2026)
            .Select(bracket => new LegalParameterBracket(
                LegalBracketKey.SolidarityFund, new DateOnly(2027, 1, 1), bracket.LowerBound, bracket.UpperBound, bracket.Rate)));

        var set = await Adapter(values, brackets).ResolveAsync(new DateOnly(2027, 1, 14), Ct);

        Assert.Equal(0.90m, set.SundaySurchargeRate.Value);
        Assert.Equal(42m, set.WeeklyHours.Value);
        Assert.Equal(new DateOnly(2025, 6, 25), set.ConnectivityAllowanceSince);
        Assert.Equal(new DateOnly(2027, 1, 1), set.Smmlv.EffectiveFrom);
    }

    // Decision 6: the brackets are yearly like the scalar keys. 2027 has every scalar row but only the 2026 bracket set.
    [Fact]
    public async Task ABracketSetFromAnEarlierYearIsLegalParameterMissing()
    {
        var values = SeededValues();
        foreach (var row in values.Where(value => value.EffectiveFrom == Y2026).ToList())
        {
            values.Add(new(row.Key, new DateOnly(2027, 1, 1), row.Value));
        }

        var exception = await Assert.ThrowsAsync<PayrollDomainException>(() =>
            Adapter(values).ResolveAsync(new DateOnly(2027, 1, 14), Ct));

        Assert.Equal("payroll.legal_parameter.missing", exception.Code);
        Assert.Contains("SolidarityFund", exception.Message);
        Assert.DoesNotContain("Smmlv", exception.Message);
    }
}
