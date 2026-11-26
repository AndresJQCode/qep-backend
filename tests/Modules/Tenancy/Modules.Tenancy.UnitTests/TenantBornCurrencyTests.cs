using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-11-23: un tenant que nace con país lleva la moneda de ese país; sin país, la de la columna.</summary>
public sealed class TenantBornCurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("CO", "COP")]
    [InlineData("co", "COP")]
    [InlineData("ES", "EUR")]
    [InlineData("DE", "EUR")]
    [InlineData("BG", "EUR")]
    [InlineData("MX", "USD")]
    [InlineData("US", "USD")]
    [InlineData("AR", "USD")]
    public void ATenantBornWithACountryTakesItsCurrency(string country, string expected)
    {
        var tenant = Create(country);

        Assert.Equal(expected, tenant.DefaultCurrency);
    }

    [Fact]
    public void ATenantBornWithoutCountryKeepsTheColumnDefault()
    {
        var tenant = Create(null);

        Assert.Equal("COP", tenant.DefaultCurrency);
        Assert.Equal("CO", tenant.Country);
    }

    [Fact]
    public void EveryCurrencyTheMappingReturnsIsInTheCatalogue()
    {
        foreach (var country in new[] { "CO", "ES", "FR", "MX", "US", "JP" })
        {
            Assert.Contains(
                Modules.Tenancy.Application.Currencies.All,
                currency => currency.Code == TenantCurrencies.ForCountry(country));
        }
    }

    private static Tenant Create(string? country) =>
        Tenant.Create(
            TenantId.New(), "org-test", "Org", "es-CO", "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now, country);
}
