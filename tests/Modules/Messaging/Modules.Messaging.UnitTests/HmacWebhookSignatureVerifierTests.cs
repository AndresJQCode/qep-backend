using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.2 y §11: HMAC-SHA256 del cuerpo crudo con el AppSecret, comparación en
/// tiempo constante; sin sección, nada pasa.</summary>
public sealed class HmacWebhookSignatureVerifierTests
{
    private const string Secret = "meta-app-secret-SENTINEL-u1";
    private const string Token = "meta-verify-token-SENTINEL-u2-0123456789abcdef";

    private static HmacWebhookSignatureVerifier Verifier(string? secret = Secret, string? token = Token) =>
        new(Options.Create(new MessagingMetaOptions { AppSecret = secret, WebhookVerifyToken = token }));

    private static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void AValidSignatureInLowerOrUpperHexPasses()
    {
        var body = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account"}""");

        Assert.True(Verifier().VerifyBody(body, Sign(body, Secret)));
        Assert.True(Verifier().VerifyBody(body, Sign(body, Secret).ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256=abc")]
    [InlineData("sha1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void AMissingOrMalformedSignatureFails(string? header) =>
        Assert.False(Verifier().VerifyBody("{}"u8, header));

    [Fact]
    public void ADifferentSecretOrADifferentBodyFails()
    {
        var body = "{}"u8.ToArray();

        Assert.False(Verifier().VerifyBody(body, Sign(body, "other")));
        Assert.False(Verifier().VerifyBody("{ }"u8, Sign(body, Secret)));
    }

    [Fact]
    public void WithoutTheSectionNothingPassesAndItIsNotConfigured()
    {
        var verifier = Verifier(secret: " ", token: null);
        var body = "{}"u8.ToArray();

        Assert.False(verifier.IsConfigured);
        Assert.False(verifier.VerifyBody(body, Sign(body, " ")));
        Assert.False(verifier.VerifyToken(null));
    }

    [Fact]
    public void TheVerifyTokenIsComparedExactly()
    {
        Assert.True(Verifier().VerifyToken(Token));
        Assert.False(Verifier().VerifyToken(Token + "x"));
        Assert.False(Verifier().VerifyToken(Token.ToUpperInvariant()));
        Assert.False(Verifier().VerifyToken(null));
    }
}
