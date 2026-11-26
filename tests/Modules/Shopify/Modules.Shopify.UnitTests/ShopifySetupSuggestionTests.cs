using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.1, plan Tarea 8 paso 4: la empresa que el asistente sugiere con lo que Shopify sabe de la tienda.
/// Función pura: un perfil roto o ausente da el nombre de la tienda y nada más (Review Focus 5).
/// </summary>
public sealed class ShopifySetupSuggestionTests
{
    private static readonly ShopifyShopAddress Address =
        new(" Mi Tienda SAS ", "Cra 1 # 2-3", "Local 4", "Medellín", "Antioquia", "ANT", "CO", "+573001112233");

    [Fact]
    public void TheSuggestionUsesTheBillingCompanyOrTheShopName()
    {
        var withCompany = ShopifySetupSuggestion.Build(new ShopifyShopProfile(null, null, "COP", true, Address), "Mi tienda", null);
        var blankCompany = ShopifySetupSuggestion.Build(
            new ShopifyShopProfile(null, null, "COP", true, Address with { Company = "   " }), "Mi tienda", null);
        var withoutProfile = ShopifySetupSuggestion.Build(null, "Mi tienda", null);

        Assert.Equal("Mi Tienda SAS", withCompany.Name);
        Assert.Equal("Mi tienda", blankCompany.Name);
        Assert.Equal("Mi tienda", withoutProfile.Name);
    }

    [Fact]
    public void TheAddressJoinsBothLinesAndTheContactEmailWins()
    {
        var suggested = ShopifySetupSuggestion.Build(
            new ShopifyShopProfile("dueno@mitienda.co", "ventas@mitienda.co", "COP", true, Address), "Mi tienda", null);

        Assert.Equal("Cra 1 # 2-3, Local 4", suggested.Address);
        Assert.Equal("ventas@mitienda.co", suggested.Email);
        Assert.Equal(("+573001112233", "CO"), (suggested.Phone, suggested.Country));
    }

    [Fact]
    public void ABrokenProfileGivesOnlyTheShopName()
    {
        var suggested = ShopifySetupSuggestion.Build(new ShopifyShopProfile(null, null, null, null, null), "Mi tienda", null);

        Assert.Equal(
            new ShopifySuggestedCompanyDto("Mi tienda", null, null, null, null, null, null, null),
            suggested);
    }

    [Fact]
    public void AnUnmatchedCityKeepsItsTextAndTheDepartment()
    {
        var department = Guid.CreateVersion7();

        var suggested = ShopifySetupSuggestion.Build(
            new ShopifyShopProfile(null, null, "COP", true, Address), "Mi tienda", new ShopifyCityLookup(department, null));

        Assert.Equal(((Guid?)null, (Guid?)department, "Medellín"), (suggested.CityId, suggested.DepartmentId, suggested.CityName));
    }

    [Fact]
    public void OnlyAColombianAddressAsksGeography()
    {
        Assert.Equal<(string, string)?>(("ANTIOQUIA", "Medellín"), ShopifySetupSuggestion.CityQuery(new ShopifyShopProfile(null, null, null, null, Address)));
        Assert.Equal<(string, string)?>(
            ("Antioquia", "Medellín"),
            ShopifySetupSuggestion.CityQuery(new ShopifyShopProfile(null, null, null, null, Address with { ProvinceCode = null })));
        Assert.Null(ShopifySetupSuggestion.CityQuery(new ShopifyShopProfile(null, null, null, null, Address with { CountryCode = "MX" })));
        Assert.Null(ShopifySetupSuggestion.CityQuery(new ShopifyShopProfile(null, null, null, null, Address with { City = " " })));
        Assert.Null(ShopifySetupSuggestion.CityQuery(null));
    }
}
