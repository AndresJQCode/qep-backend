using Bootstrapper.Cors;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>Cors:AllowedOrigins</c> es la lista exacta de orígenes desde los que el navegador puede
/// llamar a la API con la cookie de sesión. La defensa CSRF (<c>RequireCsrfHeaderMiddleware</c>)
/// depende de que esa lista no admita a nadie más: un comodín o un origen mal escrito tiene que
/// tumbar el arranque, no abrir la API en silencio.
/// </summary>
public sealed class CorsSettingsValidatorTests
{
    // Sin la clave no hay CORS: así corren las pruebas TestServer y el desarrollo local por el proxy de Vite.
    [Fact]
    public void NoOriginsIsValid()
    {
        var result = Validator(Environments.Production).Validate(null, new CorsSettings());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("https://qep.qcode.co")]
    [InlineData("https://qep.qcode.co:8443")]
    public void AnExactHttpsOriginIsValid(string origin)
    {
        var result = Validator(Environments.Production).Validate(null, With(origin));

        Assert.True(result.Succeeded);
    }

    // El mensaje nombra la clave, la posición y el valor: es lo que lee quien encuentra el pod caído.
    [Theory]
    [InlineData("*")]
    [InlineData("https://*.qcode.co")]
    [InlineData("http://qep.qcode.co")]
    [InlineData("https://qep.qcode.co/")]
    [InlineData("https://qep.qcode.co/path")]
    [InlineData("https://qep.qcode.co?x=1")]
    [InlineData("https://qep.qcode.co#app")]
    [InlineData("https://user@qep.qcode.co")]
    [InlineData("https://QEP.qcode.co")]
    [InlineData("https://qep.qcode.co:443")]
    [InlineData(" https://qep.qcode.co")]
    [InlineData("qep.qcode.co")]
    [InlineData("no-es-un-origen")]
    [InlineData("")]
    public void AnInvalidOriginFailsNamingKeyIndexAndValue(string invalid)
    {
        var result = Validator(Environments.Production).Validate(null, With("https://qep.qcode.co", invalid));

        Assert.True(result.Failed);
        Assert.Contains("Cors:AllowedOrigins:1", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains($"'{invalid}'", result.FailureMessage, StringComparison.Ordinal);
    }

    // En Development la SPA de `bun dev` (http://localhost:3002) puede llamar directo a la API sin el proxy de
    // Vite, que no reenvía el WebSocket del hub de Mensajería. Sólo localhost: el resto de las reglas no cambia.
    [Theory]
    [InlineData("http://localhost:3002")]
    [InlineData("http://localhost")]
    public void InDevelopmentAnExactHttpLocalhostOriginIsValid(string origin)
    {
        var result = Validator(Environments.Development).Validate(null, With(origin));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Local")]
    public void OutsideDevelopmentHttpLocalhostFails(string environment)
    {
        var result = Validator(environment).Validate(null, With("http://localhost:3002"));

        Assert.True(result.Failed);
        Assert.Contains("'http://localhost:3002'", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://qep.qcode.co")]
    [InlineData("http://127.0.0.1:3002")]
    [InlineData("http://localhost:3002/")]
    [InlineData("http://LOCALHOST:3002")]
    [InlineData("http://localhost.qcode.co")]
    [InlineData("http://user@localhost:3002")]
    public void InDevelopmentAnyOtherHttpOriginStillFails(string invalid)
    {
        var result = Validator(Environments.Development).Validate(null, With(invalid));

        Assert.True(result.Failed);
        Assert.Contains($"'{invalid}'", result.FailureMessage, StringComparison.Ordinal);
    }

    private static CorsSettingsValidator Validator(string environment) => new(new FixedEnvironment(environment));

    private static CorsSettings With(params string[] origins) =>
        new() { AllowedOrigins = [.. origins] };

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Bootstrapper.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
