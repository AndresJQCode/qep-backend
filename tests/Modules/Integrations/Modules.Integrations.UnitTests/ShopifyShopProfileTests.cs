using Modules.Integrations.Application.Shopify;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-11-17 §2 y plan D1: el perfil de la tienda se guarda como un campo interno JSON de la conexión
/// (<c>shopProfile</c>, hasta 4096). Leerlo nunca lanza: un perfil viejo o roto es <c>null</c>, no un 500.
/// </summary>
public sealed class ShopifyShopProfileTests
{
    private static readonly ShopifyShopProfile Full = new(
        "dueno@mitienda.co",
        "contacto@mitienda.co",
        "COP",
        "America/Bogota",
        true,
        new ShopifyShopAddress("Mi Tienda SAS", "Calle 10 # 43-12", "Local 3", "Medellín", "Antioquia", "ANT", "050021", "CO", "+573001112233"));

    [Fact]
    public void SerializeAndTryParseRoundTrip()
    {
        var json = Full.Serialize();

        Assert.Equal(Full, ShopifyShopProfile.TryParse(json));
        Assert.Contains("\"currencyCode\":\"COP\"", json, StringComparison.Ordinal);
        Assert.Contains("\"countryCode\":\"CO\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NullFieldsAreLeftOutAndReadBackAsNull()
    {
        var sparse = new ShopifyShopProfile(null, null, "COP", null, null, null);

        var json = sparse.Serialize();

        Assert.DoesNotContain("email", json, StringComparison.Ordinal);
        Assert.Equal(sparse, ShopifyShopProfile.TryParse(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"texto\"")]
    [InlineData("{\"taxesIncluded\":\"si\"}")]
    public void ABrokenProfileIsReadAsNone(string? json)
    {
        Assert.Null(ShopifyShopProfile.TryParse(json));
    }

    [Fact]
    public void LongTextsAreCutTo255AndTheJsonFitsTheField()
    {
        var text = new string('x', 1000);
        var huge = new ShopifyShopProfile(
            text, text, text, text, false, new ShopifyShopAddress(text, text, text, text, text, text, text, text, text));

        var json = huge.Serialize();
        var parsed = ShopifyShopProfile.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(ShopifyShopProfile.TextMaxLength, parsed.Email!.Length);
        Assert.Equal(ShopifyShopProfile.TextMaxLength, parsed.BillingAddress!.Phone!.Length);
        Assert.Equal(ShopifyShopProfile.TextMaxLength, parsed.BillingAddress.City!.Length);
        Assert.True(json.Length <= ShopifyShopProfile.FieldMaxLength, $"El JSON mide {json.Length}.");
    }
}
