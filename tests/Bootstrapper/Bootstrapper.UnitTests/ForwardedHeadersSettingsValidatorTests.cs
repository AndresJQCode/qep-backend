using Bootstrapper.ReverseProxy;

namespace Bootstrapper.UnitTests;

/// <summary>
/// <c>ForwardedHeaders:KnownNetworks</c> decide en qué pares se confía para tomar la IP del cliente
/// de <c>X-Forwarded-For</c>. Un valor mal escrito tiene que tumbar el arranque: ignorarlo dejaría
/// al rate limiter con un bucket por nodo sin ningún aviso.
/// </summary>
public sealed class ForwardedHeadersSettingsValidatorTests
{
    // Sin la clave no se confía en nada más que en el loopback del framework.
    [Fact]
    public void NoNetworksIsValid()
    {
        var result = new ForwardedHeadersSettingsValidator().Validate(null, new ForwardedHeadersSettings());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void IPv4AndIPv6NetworksAreValid()
    {
        var result = new ForwardedHeadersSettingsValidator()
            .Validate(null, With("10.50.0.0/24", "10.244.0.0/16", "fd00::/8"));

        Assert.True(result.Succeeded);
    }

    // El mensaje nombra la clave, la posición y el valor: es lo que lee quien encuentra el pod caído.
    [Theory]
    [InlineData("10.50.0.0")]
    [InlineData("10.50.0.0/33")]
    [InlineData("no-es-una-red")]
    [InlineData(" ")]
    public void AnInvalidNetworkFailsNamingKeyIndexAndValue(string invalid)
    {
        var result = new ForwardedHeadersSettingsValidator()
            .Validate(null, With("10.244.0.0/16", invalid));

        Assert.True(result.Failed);
        Assert.Contains("ForwardedHeaders:KnownNetworks:1", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains($"'{invalid}'", result.FailureMessage, StringComparison.Ordinal);
    }

    private static ForwardedHeadersSettings With(params string[] networks) =>
        new() { KnownNetworks = [.. networks] };
}
