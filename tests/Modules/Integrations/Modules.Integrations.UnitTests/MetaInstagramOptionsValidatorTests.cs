using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-14 §9: en Production las dos claves y un origen de retorno; la forma de la versión y de cada
/// origen en todo ambiente. Ningún mensaje lleva un valor.</summary>
public sealed class MetaInstagramOptionsValidatorTests
{
    private const string Secret = "ig-app-secret-SENTINEL-9f8e";

    private static MetaInstagramOptionsValidator Validator(bool production) =>
        new(new UnitTestHostEnvironment(production ? Environments.Production : Environments.Development));

    private static MetaInstagramOptions Complete() => new()
    {
        AppId = "700800900",
        AppSecret = Secret,
        GraphApiVersion = "v24.0",
        AllowedRedirectOrigins = ["https://qep.qcode.co", "http://localhost:3002"],
    };

    [Fact]
    public void ProductionAcceptsTheCompleteSection() =>
        Assert.True(Validator(production: true).Validate(null, Complete()).Succeeded);

    [Theory]
    [InlineData("AppId")]
    [InlineData("AppSecret")]
    public void ProductionRejectsAMissingKeyNamingItWithoutItsValue(string missing)
    {
        var options = Complete();
        typeof(MetaInstagramOptions).GetProperty(missing)!.SetValue(options, " ");

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains($"Meta:Instagram:{missing}", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsAnEmptyOriginList()
    {
        var options = Complete();
        options.AllowedRedirectOrigins = [];

        var result = Validator(production: true).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AllowedRedirectOrigins", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentAcceptsAnEmptySection() =>
        Assert.True(Validator(production: false).Validate(null, new MetaInstagramOptions()).Succeeded);

    [Theory]
    [InlineData("https://qep.qcode.co/settings")]
    [InlineData("qep.qcode.co")]
    [InlineData("ftp://qep.qcode.co")]
    [InlineData("https://qep.qcode.co/?x=1")]
    public void AnOriginWithPathQueryOrOtherSchemeIsRejectedInAnyEnvironment(string origin)
    {
        var options = new MetaInstagramOptions { AllowedRedirectOrigins = [origin] };

        var result = Validator(production: false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AllowedRedirectOrigins:0", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionShapeIsCheckedInAnyEnvironment()
    {
        var result = Validator(production: false).Validate(null, new MetaInstagramOptions { GraphApiVersion = "24" });

        Assert.True(result.Failed);
        Assert.Contains("GraphApiVersion", Assert.Single(result.Failures!), StringComparison.Ordinal);
    }

    // §11: la lista blanca compara origen (esquema, host y puerto), no la URL completa.
    [Fact]
    public void TheSettingsAllowARedirectByOriginOnly()
    {
        var settings = new InstagramAppSettings(Microsoft.Extensions.Options.Options.Create(Complete()));

        Assert.True(settings.IsConfigured);
        Assert.Equal("700800900", settings.AppId);
        Assert.True(settings.IsAllowedRedirect(new Uri("https://qep.qcode.co/settings/integrations/instagram/callback")));
        Assert.True(settings.IsAllowedRedirect(new Uri("http://localhost:3002/settings/integrations/instagram/callback")));
        Assert.False(settings.IsAllowedRedirect(new Uri("https://evil.example/settings/integrations/instagram/callback")));
        Assert.False(settings.IsAllowedRedirect(new Uri("http://qep.qcode.co/settings/integrations/instagram/callback")));
        Assert.False(settings.IsAllowedRedirect(new Uri("https://localhost:3002/x")));
    }
}
