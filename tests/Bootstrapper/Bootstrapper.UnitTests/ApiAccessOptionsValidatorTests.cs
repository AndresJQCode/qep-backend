using Bootstrapper.ApiKeys;
using Modules.ApiAccess.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Un límite en 0 o negativo hace que <c>FixedWindowRateLimiterOptions</c> lance en el primer uso, y toda llamada externa respondería 500
/// sin que el síntoma se parezca a la causa: el arranque es el lugar para enterarse.
/// </summary>
public sealed class ApiAccessOptionsValidatorTests
{
    [Fact]
    public void TheDefaultsAreValid()
    {
        var result = new ApiAccessOptionsValidator().Validate(null, new ApiAccessOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APerKeyLimitOfZeroOrLessFailsAtStartup(int limit)
    {
        var options = new ApiAccessOptions { RateLimit = { PerKeyPerMinute = limit } };

        var result = new ApiAccessOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ApiAccess:RateLimit:PerKeyPerMinute", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APerIpLimitOfZeroOrLessFailsAtStartup(int limit)
    {
        var options = new ApiAccessOptions { RateLimit = { PerIpPerMinute = limit } };

        var result = new ApiAccessOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ApiAccess:RateLimit:PerIpPerMinute", result.FailureMessage, StringComparison.Ordinal);
    }
}
