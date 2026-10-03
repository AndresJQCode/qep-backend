using Bootstrapper.Cors;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>Cors:AllowedOrigins</c> es la lista exacta de orígenes desde los que el navegador puede
/// llamar a la API con la cookie de sesión. La defensa CSRF (<c>RequireCsrfHeaderMiddleware</c>)
/// depende de que esa lista no admita a nadie más: un comodín o un origen mal escrito tiene que
/// tumbar el arranque, no abrir la API en silencio.
/// </summary>
public sealed class CorsSettingsValidatorTests
{
    // Sin la clave no hay CORS: el desarrollo local usa el proxy de Vite y las pruebas TestServer.
    [Fact]
    public void NoOriginsIsValid()
    {
        var result = new CorsSettingsValidator().Validate(null, new CorsSettings());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("https://qep.qcode.co")]
    [InlineData("https://qep.qcode.co:8443")]
    public void AnExactHttpsOriginIsValid(string origin)
    {
        var result = new CorsSettingsValidator().Validate(null, With(origin));

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
        var result = new CorsSettingsValidator().Validate(null, With("https://qep.qcode.co", invalid));

        Assert.True(result.Failed);
        Assert.Contains("Cors:AllowedOrigins:1", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains($"'{invalid}'", result.FailureMessage, StringComparison.Ordinal);
    }

    private static CorsSettings With(params string[] origins) =>
        new() { AllowedOrigins = [.. origins] };
}
