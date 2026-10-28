using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-28 «Backend contract»: parámetros obligatorios, valores conocidos y topes de rango por agrupación (D-9).</summary>
public sealed class MessagingReportQueryTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly DateOnly From = new(2026, 10, 1);

    private static GetMessagingReportSummaryQuery Query(
        DateOnly? from = null, DateOnly? to = null, string? groupBy = "day", string? channel = null, bool noFrom = false, bool noTo = false) =>
        new(TenantId, noFrom ? null : from ?? From, noTo ? null : to ?? From.AddDays(6), groupBy, channel);

    private static string[] ErrorKeys(GetMessagingReportSummaryQuery query) =>
        [.. new GetMessagingReportSummaryValidator().Validate(query).Errors.Select(e => e.PropertyName).Distinct()];

    [Fact]
    public void AValidQueryPasses() => Assert.Empty(ErrorKeys(Query()));

    [Fact]
    public void FromAndToAreRequired()
    {
        Assert.Contains("from", ErrorKeys(Query(noFrom: true)));
        Assert.Contains("to", ErrorKeys(Query(noTo: true)));
    }

    [Fact]
    public void FromCannotBeAfterTo() => Assert.Contains("from", ErrorKeys(Query(from: From.AddDays(3), to: From)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("year")]
    public void GroupByMustBeDayWeekOrMonth(string? groupBy) => Assert.Contains("groupBy", ErrorKeys(Query(groupBy: groupBy)));

    [Theory]
    [InlineData("day")]
    [InlineData("week")]
    [InlineData("month")]
    public void KnownGroupingsPass(string groupBy) => Assert.DoesNotContain("groupBy", ErrorKeys(Query(groupBy: groupBy)));

    [Theory]
    [InlineData("WhatsApp")]
    [InlineData("Instagram")]
    [InlineData(null)]
    public void KnownChannelsPass(string? channel) => Assert.DoesNotContain("channel", ErrorKeys(Query(channel: channel)));

    [Fact]
    public void AnUnknownChannelIsRejected() => Assert.Contains("channel", ErrorKeys(Query(channel: "Telegram")));

    [Theory]
    [InlineData(ReportGrouping.Day, 92, true)]
    [InlineData(ReportGrouping.Day, 93, false)]
    public void DaysAreLimitedTo92(ReportGrouping groupBy, int days, bool allowed) =>
        Assert.Equal(allowed, MessagingReportRange.IsWithinLimit(From, From.AddDays(days - 1), groupBy));

    [Fact]
    public void WeeksAreLimitedTo53Buckets()
    {
        // Lunes 5 de octubre de 2026 + 53 semanas exactas = 53 buckets; un día más abre el 54.º.
        var monday = new DateOnly(2026, 10, 5);
        Assert.True(MessagingReportRange.IsWithinLimit(monday, monday.AddDays(53 * 7 - 1), ReportGrouping.Week));
        Assert.False(MessagingReportRange.IsWithinLimit(monday, monday.AddDays(53 * 7), ReportGrouping.Week));
    }

    [Fact]
    public void MonthsAreLimitedTo24Buckets()
    {
        var first = new DateOnly(2024, 11, 1);
        Assert.True(MessagingReportRange.IsWithinLimit(first, new DateOnly(2026, 10, 31), ReportGrouping.Month));
        Assert.False(MessagingReportRange.IsWithinLimit(first, new DateOnly(2026, 11, 1), ReportGrouping.Month));
    }

    [Theory]
    [InlineData("day", ReportGrouping.Day)]
    [InlineData("WEEK", ReportGrouping.Week)]
    [InlineData("Month", ReportGrouping.Month)]
    public void GroupingParsesIgnoringCase(string text, ReportGrouping expected) =>
        Assert.Equal(expected, MessagingReportRange.ParseGrouping(text));
}
