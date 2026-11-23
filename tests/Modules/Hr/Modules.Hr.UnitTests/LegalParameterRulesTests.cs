using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParameterRulesTests
{
    [Theory]
    [InlineData("0.04")]
    [InlineData("1.00")]
    [InlineData("0.000001")]
    public void RatesMustBeAFractionUpToOne(string value) =>
        Assert.Null(LegalParameterRules.Check(LegalParameterKey.HealthEmployeeRate, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0")]
    [InlineData("1.0000001")]
    [InlineData("12")]
    [InlineData("-0.04")]
    public void ARateOutsideZeroToOneSaysItMayBeAMissingPercentFormat(string value) =>
        Assert.Equal(
            LegalParameterRules.RateOutOfRangeMessage,
            LegalParameterRules.Check(LegalParameterKey.SundaySurchargeRate, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0")]
    [InlineData("-0.04")]
    public void ABracketRateAtOrBelowZeroDoesNotSuggestAMissingPercentFormat(string rate)
    {
        var message = LegalParameterRules.CheckBrackets(
            [new(0m, 4m, 0.01m), new(4m, null, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture))]);

        Assert.Equal("La tasa del tramo 2 tiene que estar entre 0 % y 100 %, sin incluir el 0 %.", message);
    }

    [Fact]
    public void ABracketRateAboveOneSuggestsAMissingPercentFormat() =>
        Assert.Equal(
            LegalParameterRules.RateOutOfRangeMessage,
            LegalParameterRules.CheckBrackets([new(0m, 4m, 0.01m), new(4m, null, 12m)]));

    [Fact]
    public void TheRateMessageIsTheOneTheSpecFixes() =>
        Assert.Equal(
            "La tasa tiene que estar entre 0 % y 100 %. ¿Escribiste 12 en una celda sin formato de porcentaje?",
            LegalParameterRules.RateOutOfRangeMessage);

    [Theory]
    [InlineData(LegalParameterKey.Smmlv, 1_750_905)]
    [InlineData(LegalParameterKey.IntegralSalaryFloor, 22_761_765)]
    [InlineData(LegalParameterKey.TransportAllowance, 249_095)]
    [InlineData(LegalParameterKey.IbcCapSmmlv, 25)]
    [InlineData(LegalParameterKey.TransportAllowanceCapSmmlv, 2)]
    public void AmountsAndCapsMustBePositive(LegalParameterKey key, int value)
    {
        Assert.Null(LegalParameterRules.Check(key, value));
        Assert.NotNull(LegalParameterRules.Check(key, 0));
        Assert.NotNull(LegalParameterRules.Check(key, -value));
    }

    [Theory]
    [InlineData(44, true)]
    [InlineData(168, true)]
    [InlineData(0, false)]
    [InlineData(169, false)]
    public void WeeklyHoursUpTo168(int hours, bool valid) =>
        Assert.Equal(valid, LegalParameterRules.Check(LegalParameterKey.WeeklyHours, hours) is null);

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void ConnectivityIsExactlyOne(int value, bool valid) =>
        Assert.Equal(valid, LegalParameterRules.Check(LegalParameterKey.ConnectivityAllowanceSince, value) is null);

    [Theory]
    [InlineData(LegalParameterKey.HealthEmployeeRate, "0.1234567")]
    [InlineData(LegalParameterKey.Smmlv, "1750905.1234567")]
    public void MoreThanSixDecimalsIsInvalid(LegalParameterKey key, string value) =>
        Assert.NotNull(LegalParameterRules.Check(key, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(LegalParameterKey.Smmlv)]
    [InlineData(LegalParameterKey.IbcCapSmmlv)]
    public void AValueTheColumnCannotHoldIsInvalidInsteadOfAServerError(LegalParameterKey key)
    {
        Assert.NotNull(LegalParameterRules.Check(key, 10_000_000_000_000m));
        Assert.Null(LegalParameterRules.Check(key, 999_999_999_999m));
    }

    [Fact]
    public void SixDecimalsAreFine() =>
        Assert.Null(LegalParameterRules.Check(LegalParameterKey.HealthEmployeeRate, 0.123456m));

    [Fact]
    public void TrailingZerosBeyondSixDecimalsAreNotExtraPrecision() =>
        Assert.Null(LegalParameterRules.Check(LegalParameterKey.HealthEmployeeRate, 0.0400000m));

    [Fact]
    public void EnsureValidThrowsValueInvalid()
    {
        var error = Assert.Throws<HrDomainException>(() => LegalParameterRules.EnsureValid(LegalParameterKey.Smmlv, -1));

        Assert.Equal("hr.legal_parameter.value_invalid", error.Code);
    }

    [Fact]
    public void EnsureValidAcceptsAValidValue() =>
        LegalParameterRules.EnsureValid(LegalParameterKey.Smmlv, 1_750_905m);

    // Los tramos sembrados en 20261122015359_AddPayrollLegalParameters.
    private static List<LegalBracketInput> Seeded() =>
    [
        new(4m, 16m, 0.01m),
        new(16m, 17m, 0.012m),
        new(17m, 18m, 0.014m),
        new(18m, 19m, 0.016m),
        new(19m, 20m, 0.018m),
        new(20m, 25m, 0.02m),
    ];

    [Fact]
    public void TheSeededSolidaritySetIsValid()
    {
        Assert.Null(LegalParameterRules.CheckBrackets(Seeded()));
        LegalParameterRules.EnsureValidBrackets(Seeded());
    }

    [Fact]
    public void AnOpenEndedLastBracketIsValid() =>
        Assert.Null(LegalParameterRules.CheckBrackets([new(0m, 4m, 0.01m), new(4m, null, 0.02m)]));

    public static TheoryData<string, List<LegalBracketInput>> InvalidSets => new()
    {
        { "empty", [] },
        { "gap", [new(4m, 16m, 0.01m), new(17m, 18m, 0.012m)] },
        { "overlap", [new(4m, 16m, 0.01m), new(15m, 18m, 0.012m)] },
        { "unsorted", [new(16m, 17m, 0.012m), new(4m, 16m, 0.01m)] },
        { "null upper not last", [new(4m, null, 0.01m), new(16m, 17m, 0.012m)] },
        { "upper not above lower", [new(4m, 4m, 0.01m)] },
        { "negative first", [new(-1m, 4m, 0.01m)] },
        { "rate zero", [new(4m, 16m, 0m)] },
        { "rate above one", [new(4m, 16m, 1.5m)] },
        { "rate with seven decimals", [new(4m, 16m, 0.0123456m + 0.0000001m)] },
        { "bound with five decimals", [new(4m, 16.12345m, 0.01m), new(16.12345m, 20m, 0.02m)] },
        { "bound above the column", [new(4m, 100_000m, 0.01m)] },
    };

    [Theory]
    [MemberData(nameof(InvalidSets))]
    public void InvalidBracketSetsAreRejected(string _, List<LegalBracketInput> brackets)
    {
        Assert.NotNull(LegalParameterRules.CheckBrackets(brackets));
        var error = Assert.Throws<HrDomainException>(() => LegalParameterRules.EnsureValidBrackets(brackets));
        Assert.Equal("hr.legal_parameter.value_invalid", error.Code);
    }
}
