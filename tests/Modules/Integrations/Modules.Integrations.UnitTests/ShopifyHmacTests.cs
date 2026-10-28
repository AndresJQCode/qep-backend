using System.Security.Cryptography;
using System.Text;
using Modules.Integrations.Application.Shopify;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-15 §4.3 y §12: el HMAC de la query (instalación y callback) y el del cuerpo (webhook), y la forma del
/// dominio de la tienda. Viven juntos porque son las dos puertas de la query que manda Shopify.
/// </summary>
public sealed class ShopifyHmacTests
{
    private const string DocsSecret = "hush";

    // El ejemplo de la documentación de Shopify (verificación del HMAC de la query del OAuth).
    private static Dictionary<string, string> DocsQuery() => new(StringComparer.Ordinal)
    {
        ["code"] = "0907a61c0c8d55e99db179b68161bc00",
        ["shop"] = "some-shop.myshopify.com",
        ["state"] = "0.6784241404160823",
        ["timestamp"] = "1337178173",
        ["hmac"] = "700e2dadb827fcc8609e9d5ce208b2e9cdaab9df07390d2cbca10d7c328fc4bf",
    };

    [Fact]
    public void VerifyQueryKnownVectorTrue() =>
        Assert.True(ShopifyHmac.VerifyQuery(DocsQuery(), DocsSecret));

    [Fact]
    public void VerifyQueryTamperedFalse()
    {
        var query = DocsQuery();
        query["shop"] = "other-shop.myshopify.com";

        Assert.False(ShopifyHmac.VerifyQuery(query, DocsSecret));
    }

    [Fact]
    public void VerifyQueryWithOtherSecretFalse() =>
        Assert.False(ShopifyHmac.VerifyQuery(DocsQuery(), "not-hush"));

    [Fact]
    public void VerifyQueryWithoutHmacFalse()
    {
        var query = DocsQuery();
        query.Remove("hmac");

        Assert.False(ShopifyHmac.VerifyQuery(query, DocsSecret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("700e2dadb827fcc8609e9d5ce208b2e9")]
    public void VerifyQueryMalformedHmacFalse(string hmac)
    {
        var query = DocsQuery();
        query["hmac"] = hmac;

        Assert.False(ShopifyHmac.VerifyQuery(query, DocsSecret));
    }

    // El hex de Shopify viene en minúsculas, pero un hex en mayúsculas es el mismo valor.
    [Fact]
    public void VerifyQueryUpperCaseHexTrue()
    {
        var query = DocsQuery();
        query["hmac"] = query["hmac"].ToUpperInvariant();

        Assert.True(ShopifyHmac.VerifyQuery(query, DocsSecret));
    }

    [Fact]
    public void VerifyBodyKnownVectorTrue()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":820982911946154508,"email":"jon@doe.ca"}""");
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(DocsSecret), body));

        Assert.True(ShopifyHmac.VerifyBody(body, signature, DocsSecret));
    }

    [Fact]
    public void VerifyBodyTamperedFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":1}""");
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(DocsSecret), body));

        Assert.False(ShopifyHmac.VerifyBody(Encoding.UTF8.GetBytes("""{"id":2}"""), signature, DocsSecret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    public void VerifyBodyMalformedSignatureFalse(string signature) =>
        Assert.False(ShopifyHmac.VerifyBody("{}"u8, signature, DocsSecret));

    [Theory]
    [InlineData("MI-Tienda.myshopify.com", "mi-tienda.myshopify.com")]
    [InlineData(" mi-tienda.myshopify.com ", "mi-tienda.myshopify.com")]
    [InlineData("tienda2.myshopify.com", "tienda2.myshopify.com")]
    public void TryNormalizeValidShopLowercases(string input, string expected)
    {
        Assert.True(ShopifyShopDomain.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("evil.com")]
    [InlineData("mi-tienda.myshopify.com.evil.com")]
    [InlineData("-tienda.myshopify.com")]
    [InlineData("mi_tienda.myshopify.com")]
    [InlineData("https://mi-tienda.myshopify.com")]
    [InlineData("mi-tienda.myshopify.com/admin")]
    [InlineData(".myshopify.com")]
    public void TryNormalizeInvalidShopFalse(string? input) =>
        Assert.False(ShopifyShopDomain.TryNormalize(input, out _));

    [Fact]
    public void TryNormalizeSixtyFiveCharactersFalse()
    {
        // ".myshopify.com" son 14 caracteres: 51 + 14 = 65.
        var shop = new string('a', 51) + ".myshopify.com";
        Assert.Equal(65, shop.Length);

        Assert.False(ShopifyShopDomain.TryNormalize(shop, out _));
        Assert.True(ShopifyShopDomain.TryNormalize(shop[1..], out _));
    }
}
