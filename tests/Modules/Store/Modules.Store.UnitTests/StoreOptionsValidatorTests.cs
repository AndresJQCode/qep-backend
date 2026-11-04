using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Store.Application;
using Modules.Store.Infrastructure;

namespace Modules.Store.UnitTests;

/// <summary>
/// Qué exige el arranque en cada ambiente (revisión final, M-1): los secretos sólo en Production, como todos los validadores del
/// repositorio; el anti-bot apagado, prohibido en todo ambiente que no sea Development o Testing, para que Staging no arranque con
/// un captcha que acepta cualquier token.
/// </summary>
public sealed class StoreOptionsValidatorTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string[] FailuresIn(string environment, StoreOptions options)
    {
        var result = new StoreOptionsValidator(new Environment(environment)).Validate(null, options);
        return result.Failures?.ToArray() ?? [];
    }

    private static StoreOptions Configured(bool turnstile = true) => new()
    {
        Storefront = new StorefrontOptions { SharedSecret = new string('s', 32) },
        Turnstile = new TurnstileOptions { Enabled = turnstile, SecretKey = turnstile ? "turnstile-secret" : null },
    };

    [Fact]
    public void ProductionWithoutSecretsFailsBoth()
    {
        var options = new StoreOptions();

        var failures = FailuresIn("Production", options);

        Assert.Contains(failures, failure => failure.Contains("Store:Storefront:SharedSecret", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("Store:Turnstile:SecretKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void OutsideProductionMissingSecretsDoNotStopTheStartup(string environment) =>
        Assert.Empty(FailuresIn(environment, new StoreOptions()));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    public void TurnstileDisabledIsForbiddenOutsideDevelopmentAndTesting(string environment)
    {
        var failure = Assert.Single(FailuresIn(environment, Configured(turnstile: false)));

        Assert.Contains("Store:Turnstile:Enabled", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void TurnstileDisabledIsAllowedInDevelopmentAndTesting(string environment) =>
        Assert.Empty(FailuresIn(environment, Configured(turnstile: false)));

    [Fact]
    public void AShortSecretStillFailsOutsideProduction()
    {
        var options = new StoreOptions { Storefront = new StorefrontOptions { SharedSecret = new string('s', 31) } };

        Assert.Contains("Store:Storefront:SharedSecret", Assert.Single(FailuresIn("Staging", options)), StringComparison.Ordinal);
    }
}
