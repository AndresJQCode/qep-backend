using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-28: sesiones, contactos nuevos y recurrentes, los dos tiempos de respuesta, buckets, mediana y p90, asesores.</summary>
public sealed class MessagingReportAggregatorTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
    private static readonly Guid Connection = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid Laura = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid Pedro = Guid.Parse("01900000-0000-7000-8000-0000000000b2");
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    private static DateTimeOffset Bog(int m, int d, int h = 9, int min = 0) => new(2026, m, d, h, min, 0, TimeSpan.FromHours(-5));

    private static ReportWindow Window(DateOnly from, DateOnly to)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-5));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-5));
        return new ReportWindow(from, to, start, end);
    }

    private static ReportData Data(IEnumerable<ReportSessionRow>? sessions = null, IEnumerable<NewContactRow>? news = null) =>
        new((sessions ?? []).ToList(), (news ?? []).ToList());

    private static ReportSessionRow Session(
        DateTimeOffset start,
        int? anyOutSeconds = null,
        int? memberOutSeconds = null,
        Guid? member = null,
        Guid? assigned = null,
        ConversationChannel channel = ConversationChannel.WhatsApp,
        DateTimeOffset? firstEver = null,
        Guid? conversation = null) =>
        new(
            conversation ?? Guid.CreateVersion7(), Connection, channel, assigned, start, start,
            anyOutSeconds is { } a ? start.AddSeconds(a) : null,
            memberOutSeconds is { } m ? start.AddSeconds(m) : null,
            memberOutSeconds is null ? null : member,
            firstEver ?? start);

    private static MessagingReportSummaryDto Build(
        ReportData current,
        DateOnly? from = null,
        DateOnly? to = null,
        ReportGrouping groupBy = ReportGrouping.Day,
        ReportData? previous = null,
        Func<Guid, BusinessHoursSchedule?>? scheduleOf = null,
        IReadOnlyDictionary<Guid, string>? names = null,
        IReadOnlyCollection<ConversationChannel>? channelsWithData = null)
    {
        var f = from ?? new DateOnly(2026, 10, 5);
        var t = to ?? new DateOnly(2026, 10, 11);
        var days = t.DayNumber - f.DayNumber + 1;
        var prevWindow = Window(f.AddDays(-days), f.AddDays(-1));
        return MessagingReportAggregator.Build(
            Window(f, t), current, prevWindow, previous ?? Data(), groupBy, Bogota,
            scheduleOf ?? (_ => null), names ?? new Dictionary<Guid, string>(), channelsWithData);
    }

    [Fact]
    public void ChannelsWithDataListsTheChannelsInFixedOrderWithoutDuplicates()
    {
        var report = Build(Data(), channelsWithData: [ConversationChannel.Instagram, ConversationChannel.WhatsApp, ConversationChannel.Instagram]);

        Assert.Equal(["WhatsApp", "Instagram"], report.ChannelsWithData);
    }

    [Fact]
    public void ChannelsWithDataIsEmptyWhenThereIsNoData()
    {
        Assert.Empty(Build(Data()).ChannelsWithData);
        Assert.Empty(Build(Data(), channelsWithData: []).ChannelsWithData);
    }

    [Fact]
    public void ChannelsWithDataOnlyHasTheChannelsGiven()
    {
        Assert.Equal(["Instagram"], Build(Data(), channelsWithData: [ConversationChannel.Instagram]).ChannelsWithData);
    }

    [Fact]
    public void TheDailySeriesIsZeroFilledAndBucketsByTheTenantLocalDay()
    {
        // 03:00Z del 6 de octubre = 22:00 del 5 en Bogotá.
        var late = new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero);
        var report = Build(Data([Session(late), Session(Bog(10, 7))]));

        Assert.Equal(7, report.Series.Count);
        Assert.Equal(new DateOnly(2026, 10, 5), report.Series[0].Start);
        Assert.Equal([1, 0, 1, 0, 0, 0, 0], report.Series.Select(p => p.Conversations));
    }

    [Fact]
    public void WeeksStartOnMondayAndAreZeroFilled()
    {
        // Miércoles 7 al martes 20: tres semanas (5, 12 y 19 de octubre).
        var report = Build(
            Data([Session(Bog(10, 8)), Session(Bog(10, 19))]),
            new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 20), ReportGrouping.Week);

        Assert.Equal([new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 19)], report.Series.Select(p => p.Start));
        Assert.Equal([1, 0, 1], report.Series.Select(p => p.Conversations));
    }

    [Fact]
    public void MonthsStartOnTheFirst()
    {
        var report = Build(
            Data([Session(Bog(8, 20)), Session(Bog(10, 3))]),
            new DateOnly(2026, 8, 15), new DateOnly(2026, 10, 20), ReportGrouping.Month);

        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], report.Series.Select(p => p.Start));
        Assert.Equal([1, 0, 1], report.Series.Select(p => p.Conversations));
    }

    [Fact]
    public void NewContactsAreBucketedByTheirFirstInboundAndSplitByChannel()
    {
        var news = new[]
        {
            new NewContactRow(Guid.CreateVersion7(), ConversationChannel.WhatsApp, Bog(10, 6)),
            new NewContactRow(Guid.CreateVersion7(), ConversationChannel.WhatsApp, Bog(10, 6, 15)),
            new NewContactRow(Guid.CreateVersion7(), ConversationChannel.Instagram, Bog(10, 8)),
        };

        var report = Build(Data(news: news));

        Assert.Equal(3, report.Totals.Current.NewContacts);
        Assert.Equal(2, report.Totals.Current.NewContactsByChannel.WhatsApp);
        Assert.Equal(1, report.Totals.Current.NewContactsByChannel.Instagram);
        Assert.Equal([0, 2, 0, 1, 0, 0, 0], report.Series.Select(p => p.NewContacts));
    }

    [Fact]
    public void ReturningContactsAreDistinctConversationsWhoseFirstInboundIsBeforeThePeriod()
    {
        var returning = Guid.CreateVersion7();
        var fresh = Guid.CreateVersion7();
        var sessions = new[]
        {
            Session(Bog(10, 5), conversation: returning, firstEver: Bog(9, 1)),
            Session(Bog(10, 8), conversation: returning, firstEver: Bog(9, 1)),
            Session(Bog(10, 6), conversation: fresh, firstEver: Bog(10, 6)),
        };

        var report = Build(Data(sessions));

        Assert.Equal(3, report.Totals.Current.Conversations);
        Assert.Equal(1, report.Totals.Current.ReturningContacts);
    }

    [Fact]
    public void FirstResponseMeasuresFromTheStartInboundToTheFirstOutbound()
    {
        var report = Build(Data([
            Session(Bog(10, 5), anyOutSeconds: 120, memberOutSeconds: 600, member: Laura),
            Session(Bog(10, 5), anyOutSeconds: 30, memberOutSeconds: null),
        ]));

        Assert.Equal(2, report.FirstResponse.AnsweredSessions);
        Assert.Equal(30, report.FirstResponse.MedianSeconds);
        Assert.Equal(120, report.FirstResponse.P90Seconds);
        Assert.Equal(1, report.AdvisorResponse.AnsweredSessions);
        Assert.Equal(1, report.AdvisorResponse.UnansweredSessions);
        Assert.Equal(600, report.AdvisorResponse.MedianSeconds);
    }

    [Fact]
    public void ASessionWithoutAStartInboundHasNoResponseClock()
    {
        var noInbound = new ReportSessionRow(Guid.CreateVersion7(), Connection, ConversationChannel.WhatsApp, null, Bog(10, 5), null, null, null, null, null);

        var report = Build(Data([noInbound]));

        Assert.Equal(1, report.Totals.Current.Conversations);
        Assert.Equal(0, report.FirstResponse.AnsweredSessions);
        Assert.Equal(0, report.FirstResponse.UnansweredSessions);
        Assert.Equal(0, report.AdvisorResponse.UnansweredSessions);
        Assert.Empty(report.Advisors);
    }

    [Fact]
    public void WithoutResponsesMedianAndP90AreNullNotZero()
    {
        var report = Build(Data());

        Assert.Null(report.FirstResponse.MedianSeconds);
        Assert.Null(report.FirstResponse.P90Seconds);
        Assert.Null(report.AdvisorResponse.MedianSeconds);
        Assert.All(report.FirstResponse.Buckets, bucket => Assert.Equal(0, bucket.Count));
    }

    [Theory]
    [InlineData(0, "lt5m")]
    [InlineData(299, "lt5m")]
    [InlineData(300, "5to15m")]
    [InlineData(899, "5to15m")]
    [InlineData(900, "15to60m")]
    [InlineData(3599, "15to60m")]
    [InlineData(3600, "1to4h")]
    [InlineData(14399, "1to4h")]
    [InlineData(14400, "gt4h")]
    public void BucketBoundariesAreRightOpen(int seconds, string expected)
    {
        var report = Build(Data([Session(Bog(10, 5, 8), anyOutSeconds: seconds, memberOutSeconds: seconds, member: Laura)]));

        Assert.Equal(["lt5m", "5to15m", "15to60m", "1to4h", "gt4h"], report.FirstResponse.Buckets.Select(b => b.Key));
        Assert.Equal(1, report.FirstResponse.Buckets.Single(b => b.Key == expected).Count);
        Assert.Equal(1, report.AdvisorResponse.Buckets.Single(b => b.Key == expected).Count);
    }

    [Fact]
    public void MedianAndP90UseNearestRank()
    {
        // 1..10 segundos: mediana = rango 5 → 5; p90 = rango 9 → 9.
        var sessions = Enumerable.Range(1, 10).Select(i => Session(Bog(10, 5), anyOutSeconds: i)).ToList();

        var report = Build(Data(sessions));

        Assert.Equal(5, report.FirstResponse.MedianSeconds);
        Assert.Equal(9, report.FirstResponse.P90Seconds);
    }

    [Fact]
    public void TimesCountOnlyBusinessHoursWhenTheConnectionHasASchedule()
    {
        // Lunes 5, 07:00 → 08:10 con horario 08-12: 10 min de atención; de corrido serían 70.
        var schedule = BusinessHoursSchedule.Create(
            Guid.CreateVersion7(), TenantId, "Oficina",
            Enumerable.Range(1, 7).Select(d => new WeekdayHours(d, d <= 5 ? [new TimeRange(480, 720)] : [])).ToList(),
            [], null, new DateOnly(2026, 10, 1), Bog(10, 1));
        var session = Session(Bog(10, 5, 7), anyOutSeconds: 70 * 60);

        var withSchedule = Build(Data([session]), scheduleOf: _ => schedule);
        var without = Build(Data([session]));

        Assert.True(withSchedule.BusinessHoursApplied);
        Assert.Equal(600, withSchedule.FirstResponse.MedianSeconds);
        Assert.False(without.BusinessHoursApplied);
        Assert.Equal(4200, without.FirstResponse.MedianSeconds);
    }

    [Fact]
    public void AdvisorsAreAttributedToWhoAnsweredNotToTheAssignee()
    {
        var sessions = new[]
        {
            Session(Bog(10, 5), anyOutSeconds: 60, memberOutSeconds: 60, member: Laura, assigned: Pedro),
            Session(Bog(10, 5), anyOutSeconds: 1000, memberOutSeconds: 1000, member: Laura, assigned: Laura),
            Session(Bog(10, 6), anyOutSeconds: null, assigned: Laura),
            Session(Bog(10, 6), anyOutSeconds: null, assigned: null),
        };
        var names = new Dictionary<Guid, string> { [Laura] = "Laura Gómez" };

        var report = Build(Data(sessions), names: names);

        var laura = report.Advisors.Single(a => a.MemberId == Laura);
        Assert.Equal("Laura Gómez", laura.DisplayName);
        Assert.Equal(2, laura.SessionsAttended);
        Assert.Equal(60, laura.MedianAdvisorResponseSeconds);
        Assert.Equal(0.5, laura.AnsweredUnder15mRatio);
        Assert.Equal(1, laura.UnansweredAssigned);

        // Pedro estaba asignado a una sesión que respondió Laura: no atendió nada y no tiene sin responder.
        Assert.DoesNotContain(report.Advisors, a => a.MemberId == Pedro);

        var unassigned = report.Advisors[^1];
        Assert.Null(unassigned.MemberId);
        Assert.Equal("Sin asignar", unassigned.DisplayName);
        Assert.Equal(1, unassigned.UnansweredAssigned);
    }

    [Fact]
    public void AMemberWithoutANameInTheLookupIsADeletedMember()
    {
        var report = Build(Data([Session(Bog(10, 5), anyOutSeconds: 10, memberOutSeconds: 10, member: Pedro)]));

        Assert.Equal("Miembro eliminado", report.Advisors.Single(a => a.MemberId == Pedro).DisplayName);
    }

    [Fact]
    public void TheUnassignedRowIsOmittedWhenNothingIsUnansweredWithoutAssignee()
    {
        var report = Build(Data([Session(Bog(10, 5), anyOutSeconds: 10, memberOutSeconds: 10, member: Laura)]));

        Assert.DoesNotContain(report.Advisors, a => a.MemberId is null);
    }

    [Fact]
    public void ThePreviousPeriodFeedsTotalsAndTheResponseComparison()
    {
        // Periodo actual 5-11 de octubre; el anterior, 28 de septiembre al 4 de octubre.
        var previous = Data([Session(Bog(9, 30), anyOutSeconds: 200, memberOutSeconds: 400, member: Laura)]);

        var report = Build(Data([Session(Bog(10, 5), anyOutSeconds: 10), Session(Bog(10, 6), anyOutSeconds: 20)]), previous: previous);

        Assert.Equal(2, report.Totals.Current.Conversations);
        Assert.Equal(1, report.Totals.Previous.Conversations);
        Assert.Equal(200, report.FirstResponse.Previous.MedianSeconds);
        Assert.Equal(400, report.AdvisorResponse.Previous.MedianSeconds);
        Assert.Equal(new DateOnly(2026, 9, 28), report.Previous.From);
        Assert.Equal(new DateOnly(2026, 10, 4), report.Previous.To);
        Assert.Equal("America/Bogota", report.Period.TimeZone);
        Assert.Equal("day", report.Period.GroupBy);
    }
}
