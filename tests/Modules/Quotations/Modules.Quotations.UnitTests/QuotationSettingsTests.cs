using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationSettingsTests
{
    [Fact]
    public void CreateNormalisesTheCodes() =>
        Assert.Equal(
            ["EUR"],
            QuotationSettings.Create(3, new Dictionary<string, decimal> { [" eur"] = 150m }).MinimumTotals.Keys);

    [Fact]
    public void CreateRejectsLessThanOneUnit()
    {
        var exception = Assert.Throws<QuotationsDomainException>(() =>
            QuotationSettings.Create(0, new Dictionary<string, decimal>()));

        Assert.Equal("quotations.settings.minimum_units.invalid", exception.Code);
    }

    [Fact]
    public void CreateRejectsACurrencyOutsideTheCatalogue()
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            QuotationSettings.Create(6, new Dictionary<string, decimal> { ["MXN"] = 1m }));

        Assert.Equal("tenancy.currency.unsupported", exception.Code);
    }
}
