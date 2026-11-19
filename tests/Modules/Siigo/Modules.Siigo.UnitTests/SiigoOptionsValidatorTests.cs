using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Siigo.Infrastructure.Api;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Spec 2026-11-18 §8: <c>Siigo:PartnerId</c> es global de QEP ante Siigo (3 a 100 alfanuméricos, sin espacios) y es requerido en
/// <c>Production</c>, donde un token del pipeline sin resolver (<c>#{…}#</c>) cuenta como ausente. <c>Siigo:BaseUrl</c> va por HTTPS y
/// absoluta en todo ambiente: la clave de acceso viaja en el cuerpo de <c>/auth</c>.
/// </summary>
public sealed class SiigoOptionsValidatorTests
{
    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(string environment, string? partnerId, string? baseUrl = "https://api.siigo.com") =>
        new SiigoOptionsValidator(new TestHostEnvironment(environment)).Validate(null, new SiigoOptions { PartnerId = partnerId, BaseUrl = baseUrl });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("with space")]
    [InlineData("ab")]
    [InlineData("con-guion")]
    [InlineData("#{SIIGO_PARTNER_ID}#")]
    public void InProductionAMissingOrMalformedPartnerIdFails(string? partnerId)
    {
        var result = Validate("Production", partnerId);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Siigo:PartnerId", StringComparison.Ordinal));
    }

    [Fact]
    public void InProductionAPartnerIdOfMoreThanAHundredCharactersFails() =>
        Assert.True(Validate("Production", new string('a', 101)).Failed);

    [Theory]
    [InlineData("abc")]
    [InlineData("QepPartner01")]
    public void InProductionAValidPartnerIdPasses(string partnerId) => Assert.True(Validate("Production", partnerId).Succeeded);

    [Fact]
    public void InProductionAPartnerIdOfExactlyAHundredCharactersPasses() =>
        Assert.True(Validate("Production", new string('a', 100)).Succeeded);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void InDevelopmentAnEmptyPartnerIdPasses(string? partnerId) => Assert.True(Validate("Development", partnerId).Succeeded);

    // Con valor, la forma se exige en todo ambiente: un valor con espacios sería rechazado por Siigo en cada llamada.
    [Fact]
    public void InDevelopmentAPartnerIdWithSpacesStillFails() => Assert.True(Validate("Development", "with space").Failed);

    [Theory]
    [InlineData("http://api.siigo.com")]
    [InlineData("api.siigo.com")]
    [InlineData("/relativa")]
    [InlineData("ftp://api.siigo.com")]
    public void TheBaseUrlMustBeAbsoluteAndHttps(string baseUrl)
    {
        var result = Validate("Development", null, baseUrl);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Siigo:BaseUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://api.siigo.com")]
    [InlineData("https://siigo.test/")]
    public void ABlankOrHttpsBaseUrlPasses(string? baseUrl) => Assert.True(Validate("Development", null, baseUrl).Succeeded);

    [Fact]
    public void NoMessageCarriesThePartnerIdValue()
    {
        var result = Validate("Production", "with space-SENTINEL");

        Assert.DoesNotContain(result.Failures!, failure => failure.Contains("SENTINEL", StringComparison.Ordinal));
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
