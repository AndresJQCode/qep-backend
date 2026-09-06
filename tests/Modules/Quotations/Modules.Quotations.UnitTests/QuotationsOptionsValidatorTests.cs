using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Infrastructure;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El envío por WhatsApp cae a `LogWhatsAppSender` (no-op) cuando faltan las credenciales de
/// Zenvia, que es lo que deja andar a las pruebas de integración y al desarrollo local sin
/// aprovisionar nada. En producción ese mismo fallback es una trampa: la API responde 200, la
/// cotización pasa a `Sent` y el cliente nunca recibe el mensaje, sin una sola señal de error.
/// Por eso las tres claves son obligatorias sólo ahí, y su ausencia frena el arranque.
/// </summary>
public sealed class QuotationsOptionsValidatorTests
{
    private static readonly WhatsAppOptions CompleteWhatsApp = new()
    {
        ApiToken = "token",
        FromNumber = "573000000000",
        TemplateId = "template-id",
    };

    [Fact]
    public void DevelopmentWithoutWhatsAppCredentialsIsValid()
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new QuotationsOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ProductionWithCompleteWhatsAppCredentialsIsValid()
    {
        var options = new QuotationsOptions { WhatsApp = CompleteWhatsApp };

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ProductionWithoutWhatsAppCredentialsFails()
    {
        var result = ValidatorFor(Environments.Production)
            .Validate(null, new QuotationsOptions());

        Assert.True(result.Failed);
        Assert.Contains("Quotations:WhatsApp:ApiToken", result.FailureMessage);
        Assert.Contains("Quotations:WhatsApp:FromNumber", result.FailureMessage);
        Assert.Contains("Quotations:WhatsApp:TemplateId", result.FailureMessage);
    }

    [Theory]
    [InlineData(nameof(WhatsAppOptions.ApiToken))]
    [InlineData(nameof(WhatsAppOptions.FromNumber))]
    [InlineData(nameof(WhatsAppOptions.TemplateId))]
    public void ProductionWithASingleMissingWhatsAppKeyFails(string missingKey)
    {
        var options = new QuotationsOptions
        {
            WhatsApp = new WhatsAppOptions
            {
                ApiToken = missingKey == nameof(WhatsAppOptions.ApiToken)
                    ? " " : CompleteWhatsApp.ApiToken,
                FromNumber = missingKey == nameof(WhatsAppOptions.FromNumber)
                    ? " " : CompleteWhatsApp.FromNumber,
                TemplateId = missingKey == nameof(WhatsAppOptions.TemplateId)
                    ? " " : CompleteWhatsApp.TemplateId,
            },
        };

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains($"Quotations:WhatsApp:{missingKey}", result.FailureMessage);
    }

    // La regla que ya existía sigue valiendo en cualquier ambiente.
    [Fact]
    public void NonPositiveExpirationSweepFails()
    {
        var options = new QuotationsOptions
        {
            ExpirationSweepMinutes = 0,
            WhatsApp = CompleteWhatsApp,
        };

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Quotations:ExpirationSweepMinutes", result.FailureMessage);
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
