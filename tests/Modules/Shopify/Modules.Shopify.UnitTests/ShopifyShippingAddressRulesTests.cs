using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>Qué dirección de envío puede mandar el worker: las mismas reglas con las que el guardado del pedido la rechaza. El
/// código postal no es un motivo en ningún país (owner, 2026-11-25).</summary>
public sealed class ShopifyShippingAddressRulesTests
{
    [Theory]
    [InlineData("ES", null)]
    [InlineData("es", "  ")]
    public void RulesAskForTheRegionInSpain(string country, string? regionCode) =>
        Assert.Equal(ShopifyShippingAddressIssue.RegionRequired, ShopifyShippingAddressRules.Check(country, regionCode));

    [Theory]
    [InlineData("ES", "99")]
    // Un país con regiones y sin tabla: la región existe en Geography, pero no hay código que mandar.
    [InlineData("PT", "01")]
    public void RulesRejectAnUnmappedRegion(string country, string regionCode) =>
        Assert.Equal(ShopifyShippingAddressIssue.RegionUnmapped, ShopifyShippingAddressRules.Check(country, regionCode));

    [Theory]
    [InlineData("PT", null)]
    [InlineData("US", " ")]
    public void RulesAcceptACountryWithoutCatalog(string country, string? regionCode) =>
        Assert.Equal(ShopifyShippingAddressIssue.None, ShopifyShippingAddressRules.Check(country, regionCode));

    [Theory]
    [InlineData("CO", null)]
    [InlineData("co", null)]
    [InlineData("ES", "28")]
    [InlineData("es", "08")]
    public void RulesAcceptColombiaAndAMappedRegion(string country, string? regionCode) =>
        Assert.Equal(ShopifyShippingAddressIssue.None, ShopifyShippingAddressRules.Check(country, regionCode));

    [Theory]
    [InlineData("ES", "28", "M")]
    [InlineData("es", "08", "B")]
    [InlineData("ES", null, null)]
    [InlineData("PT", null, null)]
    public void TheProvinceCodeComesFromTheCountryTable(string country, string? regionCode, string? expected) =>
        Assert.Equal(expected, ShopifyShippingAddressRules.ProvinceCodeFor(country, regionCode));

    [Theory]
    [InlineData("ES", true)]
    [InlineData("es", true)]
    [InlineData("FR", false)]
    [InlineData("CO", false)]
    public void OnlySpainHasARegionCatalog(string country, bool expected) =>
        Assert.Equal(expected, ShopifyShippingAddressRules.HasRegionCatalog(country));

    [Theory]
    [InlineData("ES", "M", "28")]
    [InlineData("ES", null, null)]
    [InlineData("FR", "M", null)]
    public void TheRegionCodeComesFromTheProvinceCode(string country, string? province, string? expected) =>
        Assert.Equal(expected, ShopifyShippingAddressRules.RegionCodeFor(country, province));
}
