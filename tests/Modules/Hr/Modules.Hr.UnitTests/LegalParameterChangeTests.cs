using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

public sealed class LegalParameterChangeTests
{
    private static LegalParameterChange Record(string name, string? fileName) =>
        LegalParameterChange.Record(
            "Smmlv", new DateOnly(2027, 1, 1), LegalParameterChangeAction.Created, null, "1", Guid.NewGuid(), name,
            DateTimeOffset.UtcNow, LegalParameterChangeSource.Excel, Guid.NewGuid(), fileName);

    [Fact]
    public void NamesLongerThanTheirColumnsAreTruncatedInsteadOfFailingTheSave()
    {
        var change = Record(new string('n', 300), new string('f', 400) + ".xlsx");

        Assert.Equal(200, change.ChangedByName.Length);
        Assert.Equal(255, change.FileName!.Length);
    }

    [Fact]
    public void ShortNamesAndNoFileAreKept()
    {
        var change = Record("Ana", null);

        Assert.Equal("Ana", change.ChangedByName);
        Assert.Null(change.FileName);
    }
}
