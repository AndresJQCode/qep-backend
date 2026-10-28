using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-28: <c>GET /messaging/reports/summary</c>. <c>From</c>/<c>To</c> son días locales del tenant (inclusivos), <c>GroupBy</c>
/// es <c>day|week|month</c> y <c>Channel</c> <c>WhatsApp|Instagram</c> (ausente = ambos). Texto y no enums: un valor inválido es 422 con el mapa
/// <c>errors</c>, no un 400 opaco del binder.
/// </summary>
public sealed record GetMessagingReportSummaryQuery(Guid TenantId, DateOnly? From, DateOnly? To, string? GroupBy, string? Channel)
    : IQuery<MessagingReportSummaryDto>;

/// <summary>La clave de <c>errors</c> es el nombre del query string (<c>OverridePropertyName</c>). El tope del rango va en el handler: su 422 lleva código.</summary>
public sealed class GetMessagingReportSummaryValidator : AbstractValidator<GetMessagingReportSummaryQuery>
{
    public GetMessagingReportSummaryValidator()
    {
        RuleFor(query => query.From).NotNull().OverridePropertyName("from").WithMessage("from is required (yyyy-MM-dd).");
        RuleFor(query => query.To).NotNull().OverridePropertyName("to").WithMessage("to is required (yyyy-MM-dd).");
        RuleFor(query => query.From)
            .Must((query, from) => from is null || query.To is null || from <= query.To)
            .OverridePropertyName("from").WithMessage("from must not be after to.");
        RuleFor(query => query.GroupBy)
            .Must(value => MessagingReportRange.TryParseGrouping(value, out _))
            .OverridePropertyName("groupBy").WithMessage("groupBy must be day, week or month.");
        RuleFor(query => query.Channel)
            .Must(value => value is null or nameof(ConversationChannel.WhatsApp) or nameof(ConversationChannel.Instagram))
            .OverridePropertyName("channel").WithMessage("channel must be WhatsApp or Instagram.");
    }
}

/// <summary>Spec 2026-10-28 D-9: topes por agrupación, contados en buckets (día ≤ 92, semana ≤ 53, mes ≤ 24).</summary>
public static class MessagingReportRange
{
    public const int MaxDays = 92;
    public const int MaxWeeks = 53;
    public const int MaxMonths = 24;

    public static bool TryParseGrouping(string? text, out ReportGrouping grouping)
    {
        grouping = default;
        return text is not null && Enum.TryParse(text, ignoreCase: true, out grouping) && Enum.IsDefined(grouping);
    }

    public static ReportGrouping ParseGrouping(string text) =>
        TryParseGrouping(text, out var grouping) ? grouping : throw new ArgumentException("Unknown grouping.", nameof(text));

    public static bool IsWithinLimit(DateOnly from, DateOnly to, ReportGrouping groupBy) => groupBy switch
    {
        ReportGrouping.Day => to.DayNumber - from.DayNumber + 1 <= MaxDays,
        ReportGrouping.Week => MessagingReportAggregator.BucketStarts(from, to, groupBy).Count <= MaxWeeks,
        _ => MessagingReportAggregator.BucketStarts(from, to, groupBy).Count <= MaxMonths,
    };
}

/// <summary>
/// Las filas del reporte, extraídas con SQL (puerto de Infrastructure). <c>channel</c> <c>null</c> = ambos canales. Los instantes son
/// <c>created_at</c> (spec 2026-10-28 D-3).
/// </summary>
public interface IMessagingReportSource
{
    Task<ReportData> LoadAsync(Guid tenantId, ReportWindow window, ConversationChannel? channel, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

public sealed class GetMessagingReportSummaryHandler(
    IMessagingReportSource source,
    IBusinessHoursRepository businessHours,
    IMessagingMemberNames memberNames,
    ITenantClock tenantClock,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<GetMessagingReportSummaryQuery> validator,
    ILogger<GetMessagingReportSummaryHandler> logger)
    : IQueryHandler<GetMessagingReportSummaryQuery, MessagingReportSummaryDto>
{
    /// <summary>D-5: arriba de esto se avisa en el log; no hay 422.</summary>
    public const int SessionWarningThreshold = 250_000;

    private static readonly Action<ILogger, Guid, int, int, DateOnly, DateOnly, Exception?> LargeRange = LoggerMessage.Define<Guid, int, int, DateOnly, DateOnly>(
        LogLevel.Warning,
        new EventId(1, "MessagingReportLargeRange"),
        "Messaging report for tenant {TenantId} loaded {Sessions} sessions (> {Threshold}) for {From}..{To}.");

    public async Task<MessagingReportSummaryDto> HandleAsync(GetMessagingReportSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var from = query.From!.Value;
        var to = query.To!.Value;
        var groupBy = MessagingReportRange.ParseGrouping(query.GroupBy!);
        if (!MessagingReportRange.IsWithinLimit(from, to, groupBy))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.ReportRangeTooLarge,
                "The range exceeds the limit for this grouping: 92 days, 53 weeks or 24 months.");
        }

        ConversationChannel? channel = query.Channel is null ? null : Enum.Parse<ConversationChannel>(query.Channel);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        // D-4: el periodo anterior tiene los mismos días, justo antes.
        var days = to.DayNumber - from.DayNumber + 1;
        var current = Window(calendar, from, to);
        var previous = Window(calendar, from.AddDays(-days), from.AddDays(-1));

        var currentData = await source.LoadAsync(query.TenantId, current, channel, calendar.UtcNow, cancellationToken);
        var previousData = await source.LoadAsync(query.TenantId, previous, channel, calendar.UtcNow, cancellationToken);
        if (currentData.Sessions.Count > SessionWarningThreshold)
        {
            LargeRange(logger, query.TenantId, currentData.Sessions.Count, SessionWarningThreshold, from, to, null);
        }

        var schedules = await ScheduleResolverAsync(query.TenantId, cancellationToken);
        var memberIds = currentData.Sessions
            .SelectMany(s => new[] { s.FirstMemberId, s.AssignedMemberId })
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var names = memberIds.Length == 0
            ? new Dictionary<Guid, string>()
            : (await memberNames.FindAsync(query.TenantId, memberIds, cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value);

        return MessagingReportAggregator.Build(current, currentData, previous, previousData, groupBy, calendar.TimeZone, schedules, names);
    }

    private static ReportWindow Window(TenantCalendar calendar, DateOnly from, DateOnly to) =>
        new(from, to, calendar.StartOfDayUtc(from), calendar.EndOfDayExclusiveUtc(to));

    /// <summary>Asignación de la conexión → predeterminado del tenant → ninguno (de corrido), la misma cadena que <c>IBusinessHours</c> (D-H4).</summary>
    private async Task<Func<Guid, BusinessHoursSchedule?>> ScheduleResolverAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var schedules = await businessHours.ListAsync(tenantId, cancellationToken);
        var assignments = (await businessHours.ListAssignmentsAsync(tenantId, cancellationToken)).ToDictionary(row => row.ConnectionId, row => row.ScheduleId);
        var byId = schedules.ToDictionary(schedule => schedule.Id);
        var fallback = schedules.FirstOrDefault(schedule => schedule.IsDefault);
        return connectionId => assignments.TryGetValue(connectionId, out var scheduleId) && byId.TryGetValue(scheduleId, out var assigned)
            ? assigned
            : fallback;
    }
}
