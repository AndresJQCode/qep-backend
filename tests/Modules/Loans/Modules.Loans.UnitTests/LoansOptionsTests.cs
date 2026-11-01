using Modules.Loans.Application;

namespace Modules.Loans.UnitTests;

public sealed class LoansOptionsTests
{
    [Fact]
    public void TheDefaultsAreValid()
    {
        var options = new LoansOptions();

        Assert.Equal("Loans", LoansOptions.SectionName);
        Assert.Equal(15, options.SweepMinutes);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SweepMinutesMustBePositive(int minutes)
    {
        var failure = Assert.Single(new LoansOptions { SweepMinutes = minutes }.Validate());

        Assert.Contains("Loans:SweepMinutes", failure, StringComparison.Ordinal);
    }
}
