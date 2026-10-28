using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Infrastructure;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Lo que la API exige de <c>Quotations:*</c> para arrancar. La cuenta de Zenvia ya no es configuración: cada tenant
/// trae la suya en su conexión (plan 2026-10-21), así que producción arranca sin ninguna clave de WhatsApp.
/// </summary>
public sealed class QuotationsOptionsValidatorTests
{
    private static readonly PdfOptions CompletePdf = new()
    {
        BaseUrl = "https://qcode-pdf.qcode.co",
        ApiKey = "clave",
    };

    // Plan 2026-10-21: sin cuenta global no hay nada de WhatsApp que exigir al arrancar.
    [Fact]
    public void ProductionStartsWithoutWhatsAppCredentials()
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, new QuotationsOptions { Pdf = CompletePdf });

        Assert.True(result.Succeeded);
    }

    // La regla que ya existía sigue valiendo en cualquier ambiente.
    [Fact]
    public void NonPositiveExpirationSweepFails()
    {
        var options = new QuotationsOptions
        {
            ExpirationSweepMinutes = 0,
            Pdf = CompletePdf,
        };

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Quotations:ExpirationSweepMinutes", result.FailureMessage);
    }

    // Sin la key, `qcode-pdf` responde 401 y el envío falla con un error de dominio -- ruidoso,
    // no silencioso como el fallback de WhatsApp. Igual se exige al arrancar: que el proceso no
    // levante es más barato que descubrirlo cuando una asesora aprieta Enviar.
    [Fact]
    public void ProductionWithoutThePdfApiKeyFails()
    {
        var options = new QuotationsOptions();

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Quotations:Pdf:ApiKey", result.FailureMessage);
    }

    // Fuera de producción se puede trabajar sin el servicio: las pruebas de integración no
    // generan PDFs y nadie tiene por qué aprovisionar `qcode-pdf` para tocar otra cosa.
    [Fact]
    public void DevelopmentWithoutThePdfApiKeyIsValid()
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new QuotationsOptions());

        Assert.True(result.Succeeded);
    }

    // En cualquier ambiente: la plantilla y los datos de la cotización viajan en el cuerpo, así
    // que una base URL sin TLS los expone en tránsito.
    [Theory]
    [InlineData("http://qcode-pdf.qcode.co")]
    [InlineData("qcode-pdf.qcode.co")]
    [InlineData("")]
    public void ANonHttpsPdfBaseUrlFails(string baseUrl)
    {
        var options = new QuotationsOptions
        {
            Pdf = new PdfOptions { BaseUrl = baseUrl, ApiKey = "clave" },
        };

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Quotations:Pdf:BaseUrl", result.FailureMessage);
    }

    private static QuotationsOptionsValidator ValidatorFor(string environmentName) =>
        new(new StubHostEnvironment(environmentName));

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Modules.Quotations.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
