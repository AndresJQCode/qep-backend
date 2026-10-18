using System.Security.Cryptography;
using System.Text;
using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §5.1 y §12: la firma del cuerpo del webhook (<c>X-Shopify-Hmac-Sha256</c>, HMAC-SHA256 de los bytes
/// crudos en base64). Es la copia de <c>ShopifyHmac.VerifyBody</c> de Integrations, que Shopify no puede referenciar
/// (ShopifyLayerTests), con los mismos vectores que <c>ShopifyHmacTests</c> de Integrations: si una copia cambia, la otra prueba lo dice.</summary>
public sealed class ShopifyBodySignatureTests
{
    private const string DocsSecret = "hush";

    [Fact]
    public void VerifyKnownVectorTrue()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":820982911946154508,"email":"jon@doe.ca"}""");
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(DocsSecret), body));

        Assert.True(ShopifyBodySignature.Verify(body, signature, DocsSecret));
    }

    [Fact]
    public void VerifyTamperedFalse()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":1}""");
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(DocsSecret), body));

        Assert.False(ShopifyBodySignature.Verify(Encoding.UTF8.GetBytes("""{"id":2}"""), signature, DocsSecret));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void VerifyMalformedSignatureFalse(string? signature) =>
        Assert.False(ShopifyBodySignature.Verify("{}"u8, signature, DocsSecret));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void VerifyWithoutSecretFalse(string? secret)
    {
        var body = "{}"u8.ToArray();
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("x"), body));

        Assert.False(ShopifyBodySignature.Verify(body, signature, secret));
    }
}
