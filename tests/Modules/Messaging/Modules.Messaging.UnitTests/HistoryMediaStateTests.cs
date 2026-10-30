using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-29 §1: de un <c>media_placeholder</c> del historial, qué le dice la burbuja al asesor.</summary>
public sealed class HistoryMediaStateTests
{
    private const string Placeholder = """{"historyMedia":"placeholder"}""";
    private static readonly DateTimeOffset Created = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, false, 2, null)]
    [InlineData("""{"other":1}""", false, 2, null)]
    [InlineData("not json", false, 2, null)]
    [InlineData(Placeholder, true, 2, null)]
    [InlineData(Placeholder, false, 2, "Pending")]
    [InlineData(Placeholder, false, 14, "Pending")]
    [InlineData(Placeholder, false, 15, "Unavailable")]
    public void HistoryMediaStateRules(string? details, bool hasMedia, int daysBeforeImport, string? expected) =>
        Assert.Equal(expected, HistoryMediaState.For(details, hasMedia, Created.AddDays(-daysBeforeImport), Created));

    [Fact]
    public void OnlyTheMarkerIsAPlaceholder()
    {
        Assert.True(HistoryMediaState.IsPlaceholder(Placeholder));
        Assert.False(HistoryMediaState.IsPlaceholder("""{"historyMedia":"other"}"""));
        Assert.False(HistoryMediaState.IsPlaceholder(null));
    }
}
