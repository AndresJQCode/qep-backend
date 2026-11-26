using BuildingBlocks.Application;
using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>La clave y la fecha que llegan por la URL de la consola de valores legales (spec 2026-11-22, «Rutas»).</summary>
public sealed class LegalParameterRouteTests
{
    [Theory]
    [InlineData("Smmlv", LegalParameterKey.Smmlv)]
    [InlineData("ArlClassIIRate", LegalParameterKey.ArlClassIIRate)]
    [InlineData("NightSurchargeRate", LegalParameterKey.NightSurchargeRate)]
    public void AKeyIsTheExactEnumName(string raw, LegalParameterKey expected) =>
        Assert.Equal(expected, LegalParameterRoute.ParseKey(raw));

    [Theory]
    [InlineData("smmlv")]
    [InlineData("SMMLV")]
    [InlineData("0")]
    [InlineData("29")]
    [InlineData("-1")]
    [InlineData("Smmlv,TransportAllowance")]
    [InlineData(" Smmlv")]
    [InlineData("")]
    [InlineData("Foo")]
    public void ADifferentSpellingOrANumberIsAnUnknownKey(string raw)
    {
        var exception = Assert.Throws<ResourceNotFoundException>(() => LegalParameterRoute.ParseKey(raw));

        Assert.Equal(HrErrorCodes.LegalParameterKeyUnknown, exception.Code);
    }

    [Fact]
    public void TheBracketKeyFollowsTheSameRule()
    {
        Assert.Equal(LegalBracketKey.SolidarityFund, LegalParameterRoute.ParseBracketKey("SolidarityFund"));
        Assert.Throws<ResourceNotFoundException>(() => LegalParameterRoute.ParseBracketKey("solidarityfund"));
        Assert.Throws<ResourceNotFoundException>(() => LegalParameterRoute.ParseBracketKey("0"));
        Assert.Throws<ResourceNotFoundException>(() => LegalParameterRoute.ParseBracketKey("Smmlv"));
    }

    [Fact]
    public void ADateIsYearMonthDayInvariant() =>
        Assert.Equal(new DateOnly(2027, 1, 1), LegalParameterRoute.ParseDate("2027-01-01"));

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("01/01/2027")]
    [InlineData("2027-1-1")]
    [InlineData("20270101")]
    [InlineData("")]
    public void AnythingElseIsADateInvalid(string raw)
    {
        var exception = Assert.Throws<HrDomainException>(() => LegalParameterRoute.ParseDate(raw));

        Assert.Equal(HrErrorCodes.LegalParameterDateInvalid, exception.Code);
    }
}
