using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Infrastructure;

namespace Bootstrapper.UnitTests;

/// <summary>
/// P2 (spec 2026-09-15): con <c>Quotations:PaymentProofs:PublicLinks</c> encendida, el bucket
/// público es obligatorio en cualquier ambiente. Sin él la opción quedaría prendida sin que se
/// publique un solo comprobante, y sin ningún error.
/// </summary>
public sealed class PaymentProofsOptionsValidatorTests
{
    private const string PublicBucket = "qep-public";
    private const string PublicBaseUrl = "https://assets-qep.example.co";

    // P1: apagada, todo sigue como antes, haya o no bucket público.
    [Fact]
    public void PublicLinksOffIsValidWithoutAPublicBucket()
    {
        var result = ValidatorWith(publicBucket: string.Empty, publicBaseUrl: string.Empty)
            .Validate(null, WithPublicLinks(false));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void PublicLinksOnWithThePublicBucketIsValid()
    {
        var result = ValidatorWith(PublicBucket, PublicBaseUrl).Validate(null, WithPublicLinks(true));

        Assert.True(result.Succeeded);
    }

    // El mensaje dice qué falta y por qué: es lo que lee quien encuentra el pod caído.
    [Fact]
    public void PublicLinksOnWithoutThePublicBucketFailsNamingBothKeys()
    {
        var result = ValidatorWith(publicBucket: string.Empty, publicBaseUrl: string.Empty)
            .Validate(null, WithPublicLinks(true));

        Assert.True(result.Failed);
        Assert.Contains(
            "Storage:R2:PublicBucket is required when Quotations:PaymentProofs:PublicLinks is true",
            result.FailureMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            "Storage:R2:PublicBaseUrl is required when Quotations:PaymentProofs:PublicLinks is true",
            result.FailureMessage,
            StringComparison.Ordinal);
    }

    // StorageOptionsValidator ya exige las dos juntas; éste nombra sólo la que falta.
    [Fact]
    public void PublicLinksOnWithOnlyTheBucketFailsNamingTheBaseUrl()
    {
        var result = ValidatorWith(PublicBucket, publicBaseUrl: string.Empty)
            .Validate(null, WithPublicLinks(true));

        Assert.True(result.Failed);
        Assert.Contains("Storage:R2:PublicBaseUrl is required", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Storage:R2:PublicBucket is required", result.FailureMessage, StringComparison.Ordinal);
    }

    private static PaymentProofsOptionsValidator ValidatorWith(string publicBucket, string publicBaseUrl) =>
        new(Options.Create(new StorageOptions
        {
            R2 = new R2Options { PublicBucket = publicBucket, PublicBaseUrl = publicBaseUrl },
        }));

    private static QuotationsOptions WithPublicLinks(bool publicLinks) =>
        new() { PaymentProofs = new PaymentProofsOptions { PublicLinks = publicLinks } };
}
