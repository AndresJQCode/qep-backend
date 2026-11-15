using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>Support:PublicForm:AllowedOrigins</c> son los orígenes exactos desde los que la página pública de Soporte llama a la API, sin
/// credenciales (plan fase 3, F4). Mismas reglas que <c>Cors:AllowedOrigins</c>; además, en Production la lista no puede venir vacía
/// (la página quedaría sin API) ni con un token del pipeline sin resolver.
/// </summary>
public sealed class SupportPublicFormOptionsValidatorTests
{
    [Fact]
    public void AnExactHttpsOriginIsValidInProduction()
    {
        var result = Validator(Environments.Production).Validate(null, With("https://soporte.lulocrm.com"));

        Assert.True(result.Succeeded);
    }

    // Fuera de Production la lista puede faltar: así corren las pruebas y el desarrollo local sin la página.
    [Fact]
    public void NoOriginsIsValidOutsideProduction() =>
        Assert.True(Validator(Environments.Development).Validate(null, With()).Succeeded);

    [Fact]
    public void InProductionTheListIsRequired()
    {
        var result = Validator(Environments.Production).Validate(null, With());

        Assert.True(result.Failed);
        Assert.Contains("Support:PublicForm:AllowedOrigins", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void InProductionAnUnresolvedPipelineTokenFails()
    {
        var result = Validator(Environments.Production).Validate(null, With("#{SUPPORT_PUBLIC_ORIGIN}#"));

        Assert.True(result.Failed);
        Assert.Contains("Support:PublicForm:AllowedOrigins:0", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.lulocrm.com")]
    [InlineData("http://soporte.lulocrm.com")]
    [InlineData("https://soporte.lulocrm.com/")]
    [InlineData("https://soporte.lulocrm.com/mi-empresa")]
    [InlineData("https://SOPORTE.lulocrm.com")]
    [InlineData("")]
    public void AnInvalidOriginFailsNamingKeyIndexAndValue(string invalid)
    {
        var result = Validator(Environments.Production).Validate(null, With("https://soporte.lulocrm.com", invalid));

        Assert.True(result.Failed);
        Assert.Contains("Support:PublicForm:AllowedOrigins:1", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains($"'{invalid}'", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void InDevelopmentHttpLocalhostIsValid() =>
        Assert.True(Validator(Environments.Development).Validate(null, With("http://localhost:4321")).Succeeded);

    [Fact]
    public void OutsideDevelopmentHttpLocalhostFails() =>
        Assert.True(Validator(Environments.Production).Validate(null, With("http://localhost:4321")).Failed);

    private static SupportPublicFormOptionsValidator Validator(string environment) => new(new FixedEnvironment(environment));

    private static SupportPublicFormOptions With(params string[] origins) => new() { AllowedOrigins = [.. origins] };

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Bootstrapper.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
