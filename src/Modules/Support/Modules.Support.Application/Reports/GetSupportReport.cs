using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H24, H25): el reporte de Soporte de los tickets radicados entre <c>From</c> y <c>To</c> (fechas locales del tenant,
/// <c>YYYY-MM-DD</c>, los dos incluidos).</summary>
public sealed record GetSupportReportQuery(Guid TenantId, string? From, string? To) : IQuery<SupportReportDto>;

public sealed class GetSupportReportValidator : AbstractValidator<GetSupportReportQuery>
{
    /// <summary>El rango más largo, contando los dos extremos.</summary>
    public const int MaxDays = 366;

    public GetSupportReportValidator()
    {
        RuleFor(query => query.From).Must(text => TryParse(text, out _)).WithMessage("From is a date as YYYY-MM-DD.");
        RuleFor(query => query.To)
            .Must(text => TryParse(text, out _))
            .WithMessage("To is a date as YYYY-MM-DD.")
            .DependentRules(() => RuleFor(query => query.To)
                .Must((query, to) => !TryParse(query.From, out var from) || (TryParse(to, out var until) && from <= until))
                .WithMessage("To is not before From.")
                .DependentRules(() => RuleFor(query => query.To)
                    .Must((query, to) => !TryParse(query.From, out var from)
                        || (TryParse(to, out var until) && until.DayNumber - from.DayNumber < MaxDays))
                    .WithMessage($"The range is at most {MaxDays} days.")));
    }

    internal static bool TryParse(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

/// <summary>
/// Fase 4b (H24, H25, owner 2026-11-16, Q-3, Q-16, Q-17): las cifras de Soporte, calculadas en Soporte con sus propias tablas (sin SQL
/// cruzado). Lo ve quien tenga leer o gestionar (la política <c>support.ticket.view</c>, revalidada acá). La población son los tickets
/// cuya fecha local de radicación cae en el rango: los límites UTC son la medianoche local de <c>From</c> y la del día siguiente a
/// <c>To</c>, en la zona del tenant. «Vencidos hoy» usa la misma regla y el mismo umbral que la bandeja.
/// </summary>
public sealed class GetSupportReportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportReportRepository reports,
    SupportTicketDtoComposer composer,
    IValidator<GetSupportReportQuery> validator)
    : IQueryHandler<GetSupportReportQuery, SupportReportDto>
{
    public async Task<SupportReportDto> HandleAsync(GetSupportReportQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!GetSupportReportValidator.TryParse(query.From, out var from) || !GetSupportReportValidator.TryParse(query.To, out var to))
        {
            throw new InvalidOperationException("The report range passed validation but does not parse.");
        }

        await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var context = await composer.ReadContextAsync(query.TenantId, cancellationToken);
        var fromUtc = LocalMidnight(from, context.Clock.TimeZone);
        var toUtc = LocalMidnight(to.AddDays(1), context.Clock.TimeZone);
        var tickets = await reports.ListTicketsAsync(query.TenantId, fromUtc, toUtc, cancellationToken);
        var surveys = await reports.ListSurveysAsync(query.TenantId, fromUtc, toUtc, cancellationToken);
        return SupportReportCalculator.Compute(
            from, to, tickets, surveys, await types.ListAsync(query.TenantId, cancellationToken), context.Clock, context.Today,
            context.DueSoonBusinessDays);
    }

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

/// <summary>
/// Fase 4b (H25): las cuentas del reporte, sin base. Los totales cierran (BFF: un total que no cierra se explica con un campo):
/// <list type="bullet">
/// <item>Resuelto es un ticket Resuelto, o Cerrado desde Resuelto (uno cerrado esperando al cliente sin resolver no lo es, aunque se
/// hubiera resuelto en un ciclo anterior; uno reabierto tampoco). <c>OnTime + Late + WithoutTerm = Resolved</c>: a tiempo si la última resolución cae el día del vencimiento vigente
/// o antes; sin término si no tiene vencimiento.</item>
/// <item>El tiempo de resolución son los días hábiles <c>d</c> con <c>radicación &lt; d ≤ última resolución</c>, en bruto (sin descontar
/// pausas); promedio con un decimal y mediana.</item>
/// <item>Las encuestas son las de los tickets de la población, de cualquier ciclo: <c>Pending + Sent + WithoutSurvey + Failed + Disabled
/// = Requested</c>, con <c>Disabled</c> las «sin encuesta» porque el tenant la tenía apagada y <c>WithoutSurvey</c> el resto de las
/// «sin encuesta». <c>Answered</c> y las calificaciones cuentan las respondidas.</item>
/// </list>
/// </summary>
public static class SupportReportCalculator
{
    private static readonly SupportSurveyRating[] Ratings = Enum.GetValues<SupportSurveyRating>();

    public static SupportReportDto Compute(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<SupportReportTicketRow> tickets,
        IReadOnlyList<SupportReportSurveyRow> surveys,
        IReadOnlyList<SupportTicketType> types,
        SupportClockContext clock,
        DateOnly today,
        int dueSoonBusinessDays)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(surveys);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(clock);

        var facts = tickets.Select(ticket => Facts(ticket, clock, today, dueSoonBusinessDays)).ToList();
        var resolved = facts.Where(fact => fact.ResolvedOn is not null).ToList();
        var ticketTypes = tickets.ToDictionary(ticket => ticket.TicketId, ticket => ticket.TypeId);
        var answered = surveys.Where(survey => survey.Rating is not null).ToList();

        var byType = types
            .OrderBy(type => type.Position)
            .Select(type =>
            {
                var ofType = facts.Where(fact => fact.Ticket.TypeId == type.Id.Value).ToList();
                var resolvedOfType = ofType.Where(fact => fact.ResolvedOn is not null).ToList();
                return new SupportReportTypeRowDto(
                    type.Id.Value,
                    type.Name,
                    type.IsActive,
                    ofType.Count,
                    resolvedOfType.Count,
                    ofType.Count(fact => fact.Overdue),
                    Average(resolvedOfType.Select(fact => fact.BusinessDaysToResolve!.Value).ToList()),
                    RatingCounts(answered.Where(survey => ticketTypes.GetValueOrDefault(survey.TicketId) == type.Id.Value)));
            })
            .ToList();

        var days = resolved.Select(fact => fact.BusinessDaysToResolve!.Value).ToList();
        var resolutionTime = new SupportResolutionTimeDto(
            resolved.Count,
            Average(days),
            Median(days),
            resolved.Count(fact => fact.Ticket.DueOn is { } due && fact.ResolvedOn <= due),
            resolved.Count(fact => fact.Ticket.DueOn is { } due && fact.ResolvedOn > due),
            resolved.Count(fact => fact.Ticket.DueOn is null));

        var disabled = surveys.Count(survey =>
            survey.Status is SupportSurveyStatus.Skipped && survey.ReasonCode == SupportTicketSurvey.DisabledReasonCode);
        var satisfaction = new SupportSatisfactionDto(
            surveys.Count,
            surveys.Count(survey => survey.Status is SupportSurveyStatus.Pending),
            surveys.Count(survey => survey.Status is SupportSurveyStatus.Sent),
            surveys.Count(survey => survey.Status is SupportSurveyStatus.Skipped) - disabled,
            surveys.Count(survey => survey.Status is SupportSurveyStatus.Failed),
            disabled,
            answered.Count,
            RatingCounts(answered));

        return new SupportReportDto(
            from,
            to,
            tickets.Count,
            resolved.Count,
            facts.Count(fact => fact.Overdue),
            SupportTicketStatuses.Ordered
                .Select(status => new SupportStatusCountDto(status.ToString(), tickets.Count(ticket => ticket.Status == status)))
                .ToList(),
            byType,
            resolutionTime,
            satisfaction);
    }

    /// <summary>Los días hábiles <c>d</c> con <paramref name="receivedOn"/> &lt; d ≤ <paramref name="resolvedOn"/>.</summary>
    public static int BusinessDaysToResolve(ISupportBusinessCalendar calendar, DateOnly receivedOn, DateOnly resolvedOn)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (resolvedOn <= receivedOn)
        {
            return 0;
        }

        return SupportBusinessDays.CountStrictlyBetween(calendar, receivedOn, resolvedOn) + (calendar.IsBusinessDay(resolvedOn) ? 1 : 0);
    }

    /// <summary>La mediana con un decimal; con un número par de valores, el promedio de los dos del medio. Null sin valores.</summary>
    public static decimal? Median(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2m;
        return Math.Round(median, 1, MidpointRounding.AwayFromZero);
    }

    private static decimal? Average(List<int> values) =>
        values.Count == 0 ? null : Math.Round((decimal)values.Sum() / values.Count, 1, MidpointRounding.AwayFromZero);

    private static List<SupportRatingCountDto> RatingCounts(IEnumerable<SupportReportSurveyRow> answered)
    {
        var list = answered.ToList();
        return Ratings.Select(rating => new SupportRatingCountDto(rating.ToString(), list.Count(survey => survey.Rating == rating))).ToList();
    }

    private static TicketFacts Facts(SupportReportTicketRow ticket, SupportClockContext clock, DateOnly today, int dueSoonBusinessDays)
    {
        // Revisión del bloque D: un Cerrado cuenta sólo si se cerró desde Resuelto; reabierto y cerrado sin resolver, su ResolvedAt es
        // del ciclo anterior.
        var resolved = ticket.Status is SupportTicketStatus.Resolved
            || (ticket.Status is SupportTicketStatus.Closed && ticket.ClosedFromResolved);
        var resolvedOn = resolved && ticket.ResolvedAt is { } resolvedAt ? clock.LocalDate(resolvedAt) : (DateOnly?)null;
        var (dueState, _) = SupportDueStates.Evaluate(ticket.Status, ticket.DueOn, today, clock.Calendar, dueSoonBusinessDays);
        return new TicketFacts(
            ticket,
            resolvedOn,
            resolvedOn is { } on ? BusinessDaysToResolve(clock.Calendar, clock.LocalDate(ticket.ReceivedAt), on) : null,
            dueState is SupportDueState.Overdue);
    }

    private sealed record TicketFacts(SupportReportTicketRow Ticket, DateOnly? ResolvedOn, int? BusinessDaysToResolve, bool Overdue);
}
