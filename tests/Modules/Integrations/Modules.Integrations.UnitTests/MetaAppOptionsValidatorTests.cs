using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-09 §9: en Production las cinco claves y la forma de la versión; fuera, el
/// módulo arranca sin ellas. Ningún mensaje lleva un valor.</summary>
public sealed class MetaAppOptionsValidatorTests
{
    private const string Secret = "meta-app-secret-SENTINEL-1a2b";
    private const string Token = "meta-verify-token-SENTINEL-3c4d-0123456789abcdef";
    private const string OverrideUrl = "https://qep-api.qcode.co/api/webhooks/whatsapp";

    private static MetaAppOptionsValidator Validator(bool production) =>
        new(new FixedEnvironment(production ? Environments.Production : Environments.Development));

    private static MetaAppOptions Complete() => new()
    {
        AppId = "123",
        ConfigId = "456",
        GraphApiVersion = "v24.0",
        AppSecret = Secret,
        WebhookVerifyToken = Token,
    };

    [Fact]
    public void ProductionAcceptsTheFiveKeys() =>
        Assert.True(Validator(production: true).Validate(null, Complete()).Succeeded);

    [Theory]
    [InlineData("AppId")]
    [InlineData("ConfigId")]
    [InlineData("AppSecret")]
    [InlineData("WebhookVerifyToken")]
    public void ProductionRejectsAMissingKeyNamingItWithoutItsValue(string missing)
    {
        var options = Complete();
        typeof(MetaAppOptions).GetProperty(missing)!.SetValue(options, " ");

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        var message = string.Join(" ", result.Failures!);
        Assert.Contains($"Meta:App:{missing}", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("24.0")]
    [InlineData("v24")]
    [InlineData("V24.0")]
    public void TheVersionShapeIsEnforcedEverywhere(string version)
    {
        var options = Complete();
        options.GraphApiVersion = version;

        Assert.True(Validator(production: false).Validate(null, options).Failed);
        Assert.True(Validator(production: true).Validate(null, options).Failed);
    }

    [Fact]
    public void OutsideProductionAnEmptySectionIsValidAndNotConfigured()
    {
        var options = new MetaAppOptions();

        Assert.True(Validator(production: false).Validate(null, options).Succeeded);
        Assert.False(options.IsConfigured());
        Assert.True(Complete().IsConfigured());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutTheOverrideNothingChanges(string? value)
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = value;

        Assert.True(Validator(production: true).Validate(null, options).Succeeded);
        Assert.True(Validator(production: false).Validate(null, options).Succeeded);
        Assert.Null(options.ResolvedWebhookOverrideCallbackUrl());
    }

    [Fact]
    public void AnHttpsOverrideIsAcceptedAndResolvedTrimmed()
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = $"  {OverrideUrl}  ";

        Assert.True(Validator(production: true).Validate(null, options).Succeeded);
        Assert.True(Validator(production: false).Validate(null, options).Succeeded);
        Assert.Equal(OverrideUrl, options.ResolvedWebhookOverrideCallbackUrl());
    }

    [Theory]
    [InlineData("http://qep-api.qcode.co/api/webhooks/whatsapp")]
    [InlineData("/api/webhooks/whatsapp")]
    [InlineData("qep-api.qcode.co")]
    [InlineData("ftp://qep-api.qcode.co/x")]
    public void TheOverrideMustBeAnAbsoluteHttpsUrlEverywhere(string value)
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = value;

        foreach (var production in new[] { true, false })
        {
            var result = Validator(production).Validate(null, options);

            Assert.True(result.Failed);
            var message = string.Join(" ", result.Failures!);
            Assert.Contains("Meta:App:WebhookOverrideCallbackUrl", message, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
        }
    }

    // Meta rechaza un override_callback_uri de más de 200 caracteres: mejor que el pod lo diga al arrancar que el signup.
    [Fact]
    public void TheOverrideCannotPassTheLimitOfMeta()
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = $"https://qep-api.qcode.co/{new string('a', 176)}";
        Assert.Equal(201, options.WebhookOverrideCallbackUrl.Length);

        Assert.True(Validator(production: true).Validate(null, options).Failed);

        options.WebhookOverrideCallbackUrl = options.WebhookOverrideCallbackUrl[..200];
        Assert.True(Validator(production: true).Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("#{META_WEBHOOK_OVERRIDE_CALLBACK_URL}#")]
    [InlineData("  #{META_WEBHOOK_OVERRIDE_CALLBACK_URL}#  ")]
    public void AnUnresolvedPipelineTokenCountsAsAbsentInProduction(string value)
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = value;

        Assert.True(Validator(production: true).Validate(null, options).Succeeded);
        Assert.Null(options.ResolvedWebhookOverrideCallbackUrl());
    }

    // Fuera de Production nadie reemplaza tokens: ahí es un valor mal escrito, y se dice al arrancar.
    [Fact]
    public void OutsideProductionAPipelineTokenIsJustABadUrl()
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = "#{META_WEBHOOK_OVERRIDE_CALLBACK_URL}#";

        Assert.True(Validator(production: false).Validate(null, options).Failed);
    }

    // Un token que no se reemplazó del todo no es el del pipeline: se rechaza en vez de apagar el override en silencio.
    [Theory]
    [InlineData("#{META_WEBHOOK_OVERRIDE_CALLBACK_URL}")]
    [InlineData("x#{META_WEBHOOK_OVERRIDE_CALLBACK_URL}#")]
    public void AMangledTokenIsNotAbsent(string value)
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = value;

        Assert.True(Validator(production: true).Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(true, "#{META_WEBHOOK_VERIFY_TOKEN}#")]
    [InlineData(true, " ")]
    [InlineData(false, null)]
    [InlineData(false, " ")]
    public void TheOverrideNeedsTheVerifyToken(bool production, string? verifyToken)
    {
        var options = Complete();
        options.WebhookOverrideCallbackUrl = OverrideUrl;
        options.WebhookVerifyToken = verifyToken;

        var result = Validator(production).Validate(null, options);

        Assert.True(result.Failed);
        var message = string.Join(" ", result.Failures!);
        Assert.Contains("Meta:App:WebhookVerifyToken", message, StringComparison.Ordinal);
        Assert.DoesNotContain("#{META_WEBHOOK_VERIFY_TOKEN}#", message, StringComparison.Ordinal);
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
