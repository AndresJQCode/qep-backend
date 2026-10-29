using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-28: arma el reporte a partir de las sesiones y los contactos nuevos ya extraídos. Pura: sin E/S. El tiempo de atención
/// pasa por <see cref="BusinessHoursClock"/> con el horario de la conexión de cada sesión (<c>null</c> = de corrido). Percentiles por rango
/// más cercano (nearest-rank): el valor está en la muestra, siempre entero.
/// </summary>
public static class MessagingReportAggregator
{
    public const string UnassignedName = "Sin asignar";
    public const string DeletedMemberName = "Miembro eliminado";

    private const long FifteenMinutes = 900;

    private static readonly (string Key, long UpperExclusive)[] BucketEdges =
    [
        ("lt5m", 300), ("5to15m", 900), ("15to60m", 3600), ("1to4h", 14400), ("gt4h", long.MaxValue),
    ];

    public static MessagingReportSummaryDto Build(
        ReportWindow current,
        ReportData currentData,
        ReportWindow previous,
        ReportData previousData,
        ReportGrouping groupBy,
        TimeZoneInfo timeZone,
        Func<Guid, BusinessHoursSchedule?> scheduleOf,
        IReadOnlyDictionary<Guid, string> memberNames,
        IReadOnlyCollection<ConversationChannel>? channelsWithData = null)
    {
        var now = Measure(currentData, current, timeZone, scheduleOf);
        var before = Measure(previousData, previous, timeZone, scheduleOf);

        return new MessagingReportSummaryDto(
            new MessagingReportPeriodDto(current.From, current.To, GroupByName(groupBy), timeZone.Id),
            new MessagingReportPreviousDto(previous.From, previous.To),
            now.BusinessHoursApplied,
            new MessagingReportTotalsPairDto(Totals(currentData, current), Totals(previousData, previous)),
            Series(currentData, current, groupBy, timeZone),
            ResponseTime(now.First, before.First),
            ResponseTime(now.Advisor, before.Advisor),
            Advisors(now, memberNames),
            ChannelNames(channelsWithData));
    }

    /// <summary>Spec 2026-10-28 D-13: orden fijo WhatsApp, Instagram; sólo los presentes.</summary>
    private static string[] ChannelNames(IReadOnlyCollection<ConversationChannel>? channels) =>
        channels is null
            ? []
            : [.. new[] { ConversationChannel.WhatsApp, ConversationChannel.Instagram }.Where(channels.Contains).Select(c => c.ToString())];

    public static IReadOnlyList<DateOnly> BucketStarts(DateOnly from, DateOnly to, ReportGrouping groupBy)
    {
        var starts = new List<DateOnly>();
        for (var start = BucketStart(from, groupBy); start <= to; start = Next(start, groupBy))
        {
            starts.Add(start);
        }

        return starts;
    }

    public static DateOnly BucketStart(DateOnly date, ReportGrouping groupBy) => groupBy switch
    {
        ReportGrouping.Day => date,
        ReportGrouping.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        _ => new DateOnly(date.Year, date.Month, 1),
    };

    public static string GroupByName(ReportGrouping groupBy) => groupBy switch
    {
        ReportGrouping.Day => "day",
        ReportGrouping.Week => "week",
        _ => "month",
    };

    private static DateOnly Next(DateOnly start, ReportGrouping groupBy) => groupBy switch
    {
        ReportGrouping.Day => start.AddDays(1),
        ReportGrouping.Week => start.AddDays(7),
        _ => start.AddMonths(1),
    };

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    private static MessagingReportTotalsDto Totals(ReportData data, ReportWindow window)
    {
        var returning = data.Sessions
            .Where(s => s.FirstInboundEverAt is { } first && first < window.StartUtc)
            .Select(s => s.ConversationId)
            .Distinct()
            .Count();
        var whatsApp = data.NewContacts.Count(c => c.Channel == ConversationChannel.WhatsApp);
        var instagram = data.NewContacts.Count(c => c.Channel == ConversationChannel.Instagram);
        return new MessagingReportTotalsDto(
            data.Sessions.Count, data.NewContacts.Count, returning, new NewContactsByChannelDto(whatsApp, instagram));
    }

    private static List<MessagingReportPointDto> Series(ReportData data, ReportWindow window, ReportGrouping groupBy, TimeZoneInfo timeZone)
    {
        var sessions = data.Sessions.GroupBy(s => BucketStart(LocalDate(s.SessionStart, timeZone), groupBy)).ToDictionary(g => g.Key, g => g.Count());
        var news = data.NewContacts.GroupBy(c => BucketStart(LocalDate(c.FirstInboundAt, timeZone), groupBy)).ToDictionary(g => g.Key, g => g.Count());
        return BucketStarts(window.From, window.To, groupBy)
            .Select(start => new MessagingReportPointDto(start, sessions.GetValueOrDefault(start), news.GetValueOrDefault(start)))
            .ToList();
    }

    private static Measures Measure(ReportData data, ReportWindow window, TimeZoneInfo timeZone, Func<Guid, BusinessHoursSchedule?> scheduleOf)
    {
        _ = window;
        var first = new List<long>();
        var advisor = new List<long>();
        var firstUnanswered = 0;
        var advisorUnanswered = 0;
        var applied = false;
        var perMember = new Dictionary<Guid, List<long>>();
        var unansweredByAssignee = new Dictionary<Guid, int>();
        var unansweredUnassigned = 0;

        foreach (var session in data.Sessions)
        {
            if (session.StartInboundAt is not { } inbound)
            {
                // S3: sin entrante no hay reloj de respuesta; sigue contando como conversación pero no como sesión medible.
                continue;
            }

            var schedule = scheduleOf(session.ConnectionId);
            applied |= schedule is not null;

            if (session.FirstAnyOutboundAt is { } anyOut)
            {
                first.Add(BusinessHoursClock.BusinessSeconds(schedule, inbound, anyOut, timeZone));
            }
            else
            {
                firstUnanswered++;
            }

            if (session.FirstMemberOutboundAt is { } memberOut)
            {
                var seconds = BusinessHoursClock.BusinessSeconds(schedule, inbound, memberOut, timeZone);
                advisor.Add(seconds);
                if (session.FirstMemberId is { } memberId)
                {
                    if (!perMember.TryGetValue(memberId, out var list))
                    {
                        perMember[memberId] = list = [];
                    }

                    list.Add(seconds);
                }
            }
            else
            {
                advisorUnanswered++;
                if (session.AssignedMemberId is { } assignee)
                {
                    unansweredByAssignee[assignee] = unansweredByAssignee.GetValueOrDefault(assignee) + 1;
                }
                else
                {
                    unansweredUnassigned++;
                }
            }
        }

        return new Measures(
            applied, new Times(first, firstUnanswered), new Times(advisor, advisorUnanswered), perMember, unansweredByAssignee, unansweredUnassigned);
    }

    private static ResponseTimeDto ResponseTime(Times now, Times before) => new(
        now.Seconds.Count,
        now.Unanswered,
        Percentile(now.Seconds, 0.5),
        Percentile(now.Seconds, 0.9),
        new ResponsePreviousDto(Percentile(before.Seconds, 0.5), Percentile(before.Seconds, 0.9)),
        BucketEdges.Select((edge, index) => new ResponseBucketDto(
            edge.Key,
            now.Seconds.Count(s => s < edge.UpperExclusive && (index == 0 || s >= BucketEdges[index - 1].UpperExclusive)))).ToList());

    private static List<MessagingReportAdvisorDto> Advisors(Measures measures, IReadOnlyDictionary<Guid, string> names)
    {
        var members = measures.PerMember.Keys.Union(measures.UnansweredByAssignee.Keys);
        var rows = members.Select(memberId =>
        {
            var times = measures.PerMember.GetValueOrDefault(memberId) ?? [];
            return new MessagingReportAdvisorDto(
                memberId,
                names.TryGetValue(memberId, out var name) ? name : DeletedMemberName,
                times.Count,
                Percentile(times, 0.5),
                times.Count == 0 ? null : (double)times.Count(t => t < FifteenMinutes) / times.Count,
                measures.UnansweredByAssignee.GetValueOrDefault(memberId));
        })
        .OrderByDescending(a => a.SessionsAttended)
        .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

        if (measures.UnansweredUnassigned > 0)
        {
            rows.Add(new MessagingReportAdvisorDto(null, UnassignedName, 0, null, null, measures.UnansweredUnassigned));
        }

        return rows;
    }

    private static long? Percentile(List<long> values, double p)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Max(rank, 1) - 1];
    }

    private sealed record Times(List<long> Seconds, int Unanswered);

    private sealed record Measures(
        bool BusinessHoursApplied,
        Times First,
        Times Advisor,
        Dictionary<Guid, List<long>> PerMember,
        Dictionary<Guid, int> UnansweredByAssignee,
        int UnansweredUnassigned);
}
