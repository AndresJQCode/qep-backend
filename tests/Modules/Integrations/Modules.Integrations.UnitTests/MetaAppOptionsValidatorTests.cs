using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-09 §9: en Production las cinco claves y la forma de la versión; fuera, el
/// módulo arranca sin ellas. Ningún mensaje lleva un valor.</summary>
public sealed class MetaAppOptionsValidatorTests
{
    private const string Secret = "meta-app-secret-SENTINEL-1a2b";
    private const string Token = "meta-verify-token-SENTINEL-3c4d-0123456789abcdef";

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

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
