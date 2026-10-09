using Modules.Integrations.Infrastructure.Zenvia;

namespace Modules.Integrations.UnitTests;

/// <summary>P17: el token viaja en un header, así que la URL base de la prueba es https absoluta.</summary>
public sealed class ZenviaOptionsValidatorTests
{
    [Theory]
    [InlineData("https://api.zenvia.com", true)]
    [InlineData("https://zenvia.test/", true)]
    [InlineData("http://api.zenvia.com", false)]
    [InlineData("api.zenvia.com", false)]
    [InlineData("", false)]
    public void TheBaseUrlIsAbsoluteHttps(string baseUrl, bool valid)
    {
        var result = new ZenviaOptionsValidator().Validate(null, new ZenviaOptions { BaseUrl = baseUrl });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains("Integrations:Zenvia:BaseUrl", result.FailureMessage, StringComparison.Ordinal);
        }
    }
}
