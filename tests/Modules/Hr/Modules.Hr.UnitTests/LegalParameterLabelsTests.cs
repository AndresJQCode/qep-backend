using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParameterLabelsTests
{
    [Fact]
    public void EveryKeyHasALabel()
    {
        var keys = Enum.GetValues<LegalParameterKey>();

        Assert.Equal(30, keys.Length);
        foreach (var key in keys)
        {
            var label = LegalParameterLabels.Of(key);
            Assert.False(string.IsNullOrWhiteSpace(label.Name));
        }
    }

    [Fact]
    public void YearlyKeysAreTheTwentySevenThatPayrollAndHrRequire()
    {
        var yearly = LegalParameterLabels.YearlyKeys;

        Assert.Equal(27, yearly.Count);
        Assert.DoesNotContain(LegalParameterKey.SundaySurchargeRate, yearly);
        Assert.DoesNotContain(LegalParameterKey.WeeklyHours, yearly);
        Assert.DoesNotContain(LegalParameterKey.ConnectivityAllowanceSince, yearly);
        Assert.Contains(LegalParameterKey.IntegralSalaryFloor, yearly);
        Assert.Equal(yearly.OrderBy(k => (int)k), yearly);
    }

    [Fact]
    public void GroupsAndUnitsFollowTheSpec()
    {
        Assert.Equal(
            new LegalParameterLabel(
                "Salario mínimo mensual legal vigente", LegalParameterGroup.SalaryAndCaps, LegalParameterUnit.Currency, true),
            LegalParameterLabels.Of(LegalParameterKey.Smmlv));
        Assert.Equal(LegalParameterGroup.SalaryAndCaps, LegalParameterLabels.Of(LegalParameterKey.NonSalaryCapRate).Group);
        var connectivity = LegalParameterLabels.Of(LegalParameterKey.ConnectivityAllowanceSince);
        Assert.Equal(LegalParameterGroup.Allowances, connectivity.Group);
        Assert.Equal(LegalParameterUnit.Flag, connectivity.Unit);
        Assert.False(connectivity.Yearly);
        Assert.Equal(LegalParameterUnit.Hours, LegalParameterLabels.Of(LegalParameterKey.WeeklyHours).Unit);
        Assert.Equal(LegalParameterUnit.SmmlvMultiple, LegalParameterLabels.Of(LegalParameterKey.IbcCapSmmlv).Unit);
        Assert.Equal(LegalParameterUnit.Fraction, LegalParameterLabels.Of(LegalParameterKey.SundaySurchargeRate).Unit);
        Assert.Equal(LegalParameterGroup.WorkingTime, LegalParameterLabels.Of(LegalParameterKey.NightSurchargeRate).Group);
        Assert.Equal(LegalParameterGroup.Provisions, LegalParameterLabels.Of(LegalParameterKey.PremiumProvisionRate).Group);
        Assert.Equal(LegalParameterGroup.Contributions, LegalParameterLabels.Of(LegalParameterKey.SenaRate).Group);
    }

    [Fact]
    public void TheSolidarityFundHasAName() =>
        Assert.Equal("Fondo de solidaridad pensional", LegalParameterLabels.NameOf(LegalBracketKey.SolidarityFund));
}
