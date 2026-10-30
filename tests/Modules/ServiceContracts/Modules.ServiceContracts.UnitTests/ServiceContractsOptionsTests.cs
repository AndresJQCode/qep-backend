using Modules.ServiceContracts.Application;

namespace Modules.ServiceContracts.UnitTests;

public sealed class ServiceContractsOptionsTests
{
    [Fact]
    public void TheDefaultsAreValid()
    {
        var options = new ServiceContractsOptions();

        Assert.Equal(15, options.SweepMinutes);
        Assert.Equal(8, options.SendHourLocal);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SweepMinutesMustBePositive(int minutes)
    {
        var failure = Assert.Single(new ServiceContractsOptions { SweepMinutes = minutes }.Validate());

        Assert.Contains("Billing:SweepMinutes", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void SendHourLocalMustBeAnHourOfTheDay(int hour)
    {
        var failure = Assert.Single(new ServiceContractsOptions { SendHourLocal = hour }.Validate());

        Assert.Contains("Billing:SendHourLocal", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public void TheEdgesOfTheDayAreValidSendHours(int hour)
    {
        Assert.Empty(new ServiceContractsOptions { SendHourLocal = hour }.Validate());
    }
}
