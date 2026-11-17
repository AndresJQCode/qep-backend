using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Purchases.Infrastructure.Pdf;

namespace Modules.Purchases.UnitTests;

/// <summary>
/// Copia de <c>SupportPdfOptionsValidatorTests</c>: lo que la API exige de <c>Purchases:Pdf</c> para arrancar. Compras tiene su propia
/// clave de <c>qcode-pdf</c>, nunca la de cotizaciones ni la de Soporte; sin ella en <c>Production</c> el pod no arranca, y un token
/// del pipeline sin resolver (<c>#{…}#</c>) cuenta como ausente. La base va por HTTPS en todo ambiente: la orden, con sus costos y el
/// proveedor, viaja en el cuerpo del POST.
/// </summary>
public sealed class PurchasesPdfOptionsValidatorTests
{
    [Fact]
    public void ProductionWithoutApiKeyFails()
    {
        var missing = ValidatorFor(Environments.Production).Validate(null, new PurchasesPdfOptions());
        var present = ValidatorFor(Environments.Production).Validate(null, new PurchasesPdfOptions { ApiKey = "clave" });

        Assert.True(missing.Failed);
        Assert.Contains("Purchases:Pdf:ApiKey", missing.FailureMessage);
        Assert.True(present.Succeeded);
        Assert.Equal("Purchases:Pdf", PurchasesPdfOptions.SectionName);
        Assert.Equal("https://qcode-pdf.qcode.co", new PurchasesPdfOptions().BaseUrl);
        Assert.Equal(30, new PurchasesPdfOptions().TimeoutSeconds);
    }

    [Theory]
    [InlineData("#{PURCHASES_PDF_API_KEY}#")]
    [InlineData("  #{PURCHASES_PDF_API_KEY}#  ")]
    public void ProductionWithAnUnresolvedPipelineTokenFails(string apiKey)
    {
        var result = ValidatorFor(Environments.Production).Validate(null, new PurchasesPdfOptions { ApiKey = apiKey });

        Assert.True(result.Failed);
        Assert.Contains("Purchases:Pdf:ApiKey", result.FailureMessage);
        Assert.DoesNotContain(apiKey.Trim(), result.FailureMessage);
    }

    [Theory]
    [InlineData("http://qcode-pdf.qcode.co")]
    [InlineData("qcode-pdf.qcode.co")]
    [InlineData("")]
    public void ABaseUrlThatIsNotHttpsFails(string baseUrl)
    {
        var production = ValidatorFor(Environments.Production)
            .Validate(null, new PurchasesPdfOptions { BaseUrl = baseUrl, ApiKey = "clave" });
        var development = ValidatorFor(Environments.Development).Validate(null, new PurchasesPdfOptions { BaseUrl = baseUrl });

        Assert.True(production.Failed);
        Assert.Contains("Purchases:Pdf:BaseUrl", production.FailureMessage);
        Assert.True(development.Failed);
    }

    [Fact]
    public void OutsideProductionAnEmptyKeyIsAccepted()
    {
        Assert.True(ValidatorFor(Environments.Development).Validate(null, new PurchasesPdfOptions()).Succeeded);
        Assert.True(ValidatorFor("Testing").Validate(null, new PurchasesPdfOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void TheTimeoutIsBetweenOneAnd120Seconds(int seconds)
    {
        var result = ValidatorFor(Environments.Development).Validate(null, new PurchasesPdfOptions { TimeoutSeconds = seconds });

        Assert.True(result.Failed);
        Assert.Contains("Purchases:Pdf:TimeoutSeconds", result.FailureMessage);
    }

    private static PurchasesPdfOptionsValidator ValidatorFor(string environmentName) => new(new StubHostEnvironment(environmentName));

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Modules.Purchases.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
