using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Support.Infrastructure.Pdf;

namespace Modules.Support.UnitTests;

/// <summary>
/// Fase 4b (H12): lo que la API exige de <c>Support:Pdf</c> para arrancar. Soporte tiene su propia clave de <c>qcode-pdf</c>, nunca la
/// de cotizaciones; sin ella en <c>Production</c> el pod no arranca, y un token del pipeline sin resolver (<c>#{…}#</c>) cuenta como
/// ausente. La base va por HTTPS en todo ambiente: la carta entera, con los datos del solicitante, viaja en el cuerpo del POST.
/// </summary>
public sealed class SupportPdfOptionsValidatorTests
{
    [Fact]
    public void TheKeyIsRequiredInProduction()
    {
        var missing = ValidatorFor(Environments.Production).Validate(null, new SupportPdfOptions());
        var present = ValidatorFor(Environments.Production).Validate(null, new SupportPdfOptions { ApiKey = "clave" });

        Assert.True(missing.Failed);
        Assert.Contains("Support:Pdf:ApiKey", missing.FailureMessage);
        Assert.True(present.Succeeded);
        Assert.Equal("Support:Pdf", SupportPdfOptions.SectionName);
        Assert.Equal("https://qcode-pdf.qcode.co", new SupportPdfOptions().BaseUrl);
        Assert.Equal(30, new SupportPdfOptions().TimeoutSeconds);
    }

    [Theory]
    [InlineData("#{SUPPORT_PDF_API_KEY}#")]
    [InlineData("  #{SUPPORT_PDF_API_KEY}#  ")]
    public void AnUnresolvedPipelineTokenCountsAsMissing(string apiKey)
    {
        var result = ValidatorFor(Environments.Production).Validate(null, new SupportPdfOptions { ApiKey = apiKey });

        Assert.True(result.Failed);
        Assert.Contains("Support:Pdf:ApiKey", result.FailureMessage);
        Assert.DoesNotContain(apiKey.Trim(), result.FailureMessage);
    }

    [Theory]
    [InlineData("http://qcode-pdf.qcode.co")]
    [InlineData("qcode-pdf.qcode.co")]
    [InlineData("")]
    public void TheBaseUrlMustBeHttps(string baseUrl)
    {
        var production = ValidatorFor(Environments.Production)
            .Validate(null, new SupportPdfOptions { BaseUrl = baseUrl, ApiKey = "clave" });
        var development = ValidatorFor(Environments.Development).Validate(null, new SupportPdfOptions { BaseUrl = baseUrl });

        Assert.True(production.Failed);
        Assert.Contains("Support:Pdf:BaseUrl", production.FailureMessage);
        Assert.True(development.Failed);
    }

    [Fact]
    public void OutsideProductionAnEmptyKeyIsAccepted()
    {
        Assert.True(ValidatorFor(Environments.Development).Validate(null, new SupportPdfOptions()).Succeeded);
        Assert.True(ValidatorFor("Testing").Validate(null, new SupportPdfOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void TheTimeoutIsBetweenOneAnd120Seconds(int seconds)
    {
        var result = ValidatorFor(Environments.Development).Validate(null, new SupportPdfOptions { TimeoutSeconds = seconds });

        Assert.True(result.Failed);
        Assert.Contains("Support:Pdf:TimeoutSeconds", result.FailureMessage);
    }

    private static SupportPdfOptionsValidator ValidatorFor(string environmentName) => new(new StubHostEnvironment(environmentName));

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Modules.Support.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
