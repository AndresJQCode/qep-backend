using Modules.Tenancy.Application;

namespace Modules.Tenancy.UnitTests;

public sealed class RegionalDateFormatTests
{
    [Fact]
    public void PatternKeepsKnownFormatsAndFallsBackToDayFirst()
    {
        Assert.Equal("yyyy-MM-dd", RegionalDateFormat.Pattern("yyyy-MM-dd"));
        Assert.Equal("dd/MM/yyyy", RegionalDateFormat.Pattern("dd/MM/yyyy"));
        Assert.Equal("MM/dd/yyyy", RegionalDateFormat.Pattern("MM/dd/yyyy"));
        Assert.Equal("dd/MM/yyyy", RegionalDateFormat.Pattern(null));
        Assert.Equal("dd/MM/yyyy", RegionalDateFormat.Pattern(""));
        Assert.Equal("dd/MM/yyyy", RegionalDateFormat.Pattern("dd.MM.yy"));
        Assert.Equal("MM/dd/yyyy HH:mm", RegionalDateFormat.WithTime("MM/dd/yyyy"));
        Assert.Equal("dd/MM/yyyy HH:mm", RegionalDateFormat.WithTime(null));
    }
}
