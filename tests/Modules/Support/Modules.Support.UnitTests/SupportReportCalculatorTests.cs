using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>
/// Fase 4b, Tarea 15 (H25, owner 2026-11-16, Q-17): las cifras del reporte de Soporte. Los totales cierran: a tiempo, tarde y sin término
/// suman los resueltos; pendientes, mandadas, sin encuesta, fallidas y apagadas suman las pedidas. El tiempo de resolución son los días
/// hábiles después de la radicación hasta la última resolución, incluida, en bruto (sin descontar pausas).
/// </summary>
public sealed class SupportReportCalculatorTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly DateOnly From = new(2026, 11, 1);

    private static readonly DateOnly To = new(2026, 11, 30);

    // Viernes 13 a las 10:00 en Bogotá.
    private static readonly DateTimeOffset Friday = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<SupportTicketType> Types = SupportTicketTypeDefaults.For(TenantId, Friday);

    [Fact]
    public void TheBusinessDaysToResolveCountFromTheDayAfterReceptionToTheResolutionDay()
    {
        var received = new DateOnly(2026, 11, 13);

        Assert.Equal(0, SupportReportCalculator.BusinessDaysToResolve(FakeCalendar.WithHolidays, received, received));
        // El sábado, el domingo y el lunes festivo no cuentan; el martes 17 sí.
        Assert.Equal(0, SupportReportCalculator.BusinessDaysToResolve(FakeCalendar.WithHolidays, received, new DateOnly(2026, 11, 16)));
        Assert.Equal(1, SupportReportCalculator.BusinessDaysToResolve(FakeCalendar.WithHolidays, received, new DateOnly(2026, 11, 17)));
        Assert.Equal(4, SupportReportCalculator.BusinessDaysToResolve(FakeCalendar.WithHolidays, received, new DateOnly(2026, 11, 20)));
    }

    [Fact]
    public void TheMedianOfAnEvenCount()
    {
        Assert.Equal(2.5m, SupportReportCalculator.Median([10, 1, 3, 2]));
        Assert.Equal(3m, SupportReportCalculator.Median([5, 1, 3]));
        Assert.Null(SupportReportCalculator.Median([]));
    }

    [Fact]
    public void OnTimeLateAndWithoutTermAddUpToResolved()
    {
        var petition = Types[0].Id.Value;
        var tickets = new List<SupportReportTicketRow>
        {
            // A tiempo: resuelto el 17 con vencimiento el 20.
            Ticket(petition, SupportTicketStatus.Resolved, Friday.AddDays(4), new DateOnly(2026, 11, 20)),
            // Tarde: cerrado, resuelto el 20 con vencimiento el 17.
            Ticket(petition, SupportTicketStatus.Closed, Friday.AddDays(7), new DateOnly(2026, 11, 17)),
            // Sin término.
            Ticket(petition, SupportTicketStatus.Resolved, Friday.AddDays(4), null),
            // Reabierto después de resolverlo: no cuenta como resuelto.
            Ticket(petition, SupportTicketStatus.Open, Friday.AddDays(4), new DateOnly(2026, 11, 20)),
            // Abierto y vencido hoy (el 24).
            Ticket(petition, SupportTicketStatus.Open, null, new DateOnly(2026, 11, 20)),
        };

        var report = Compute(tickets, []);

        Assert.Equal((5, 3), (report.Received, report.Resolved));
        Assert.Equal(
            (3, 1, 1, 1),
            (report.ResolutionTime.Resolved, report.ResolutionTime.OnTime, report.ResolutionTime.Late, report.ResolutionTime.WithoutTerm));
        Assert.Equal(
            report.ResolutionTime.Resolved,
            report.ResolutionTime.OnTime + report.ResolutionTime.Late + report.ResolutionTime.WithoutTerm);
        // Días hábiles: 1 (el 17), 4 (el 20) y 1: promedio 2,0 y mediana 1.
        Assert.Equal((2.0m, 1m), (report.ResolutionTime.AverageBusinessDays, report.ResolutionTime.MedianBusinessDays));
        Assert.Equal(2, report.OverdueNow);
        Assert.Equal(
            ["New", "Open", "WaitingOnCustomer", "WaitingInternal", "Resolved", "Closed"],
            report.ByStatus.Select(item => item.Status));
        Assert.Equal([0, 2, 0, 0, 2, 1], report.ByStatus.Select(item => item.Count));
        var byType = report.ByType.Single(row => row.TypeId == petition);
        Assert.Equal((5, 3, 2, (decimal?)2.0m), (byType.Received, byType.Resolved, byType.OverdueNow, byType.AverageResolutionBusinessDays));
        Assert.Equal(Types.Count, report.ByType.Count);
        Assert.All(report.ByType.Where(row => row.TypeId != petition), row => Assert.Equal((0, (decimal?)null), (row.Received, row.AverageResolutionBusinessDays)));
    }

    // Revisión del bloque D: Resuelto → Abierto → Esperando al cliente → Cerrado es un camino válido (AllowedFrom), y el ticket
    // conserva el resolved_at del ciclo anterior. Cerrado sin pasar por una resolución nueva no es un resuelto: sólo cuenta el cierre que
    // viene de Resuelto.
    [Fact]
    public void AClosedTicketThatWasReopenedIsNotResolved()
    {
        Assert.Contains(SupportTicketStatus.Open, SupportTicketStatuses.AllowedFrom(SupportTicketStatus.Resolved));
        Assert.Contains(SupportTicketStatus.Closed, SupportTicketStatuses.AllowedFrom(SupportTicketStatus.WaitingOnCustomer));
        var petition = Types[0].Id.Value;
        var closedFromResolved = Ticket(petition, SupportTicketStatus.Closed, Friday.AddDays(4), new DateOnly(2026, 11, 20));
        var closedAfterReopening = Ticket(petition, SupportTicketStatus.Closed, Friday.AddDays(4), new DateOnly(2026, 11, 20)) with
        {
            ClosedFromResolved = false,
        };

        var report = Compute([closedFromResolved, closedAfterReopening], []);

        Assert.Equal((2, 1), (report.Received, report.Resolved));
        Assert.Equal((1, 1, 0, 0), (report.ResolutionTime.Resolved, report.ResolutionTime.OnTime, report.ResolutionTime.Late,
            report.ResolutionTime.WithoutTerm));
        Assert.Equal(2, report.ByStatus.Single(item => item.Status == "Closed").Count);
    }

    [Fact]
    public void SurveyBucketsAddUpToRequested()
    {
        var petition = Types[0].Id.Value;
        var ticket = Ticket(petition, SupportTicketStatus.Closed, Friday.AddDays(4), null);
        var surveys = new List<SupportReportSurveyRow>
        {
            new(ticket.TicketId, SupportSurveyStatus.Pending, null, null),
            new(ticket.TicketId, SupportSurveyStatus.Sent, null, null),
            new(ticket.TicketId, SupportSurveyStatus.Sent, null, SupportSurveyRating.Good),
            new(ticket.TicketId, SupportSurveyStatus.Sent, null, SupportSurveyRating.Bad),
            new(ticket.TicketId, SupportSurveyStatus.Skipped, "template_not_approved", null),
            new(ticket.TicketId, SupportSurveyStatus.Skipped, SupportTicketSurvey.DisabledReasonCode, null),
            new(ticket.TicketId, SupportSurveyStatus.Failed, "send_failed", null),
        };

        var satisfaction = Compute([ticket], surveys).Satisfaction;

        Assert.Equal((7, 1, 3, 1, 1, 1, 2),
            (satisfaction.Requested, satisfaction.Pending, satisfaction.Sent, satisfaction.WithoutSurvey, satisfaction.Failed,
                satisfaction.Disabled, satisfaction.Answered));
        Assert.Equal(
            satisfaction.Requested,
            satisfaction.Pending + satisfaction.Sent + satisfaction.WithoutSurvey + satisfaction.Failed + satisfaction.Disabled);
        Assert.Equal(["Good", "Regular", "Bad"], satisfaction.Ratings.Select(item => item.Rating));
        Assert.Equal([1, 0, 1], satisfaction.Ratings.Select(item => item.Count));
        var byType = Compute([ticket], surveys).ByType.Single(row => row.TypeId == petition);
        Assert.Equal([1, 0, 1], byType.Ratings.Select(item => item.Count));
    }

    private static SupportReportDto Compute(IReadOnlyList<SupportReportTicketRow> tickets, IReadOnlyList<SupportReportSurveyRow> surveys) =>
        SupportReportCalculator.Compute(From, To, tickets, surveys, Types, Clock, new DateOnly(2026, 11, 24), dueSoonBusinessDays: 3);

    private static SupportReportTicketRow Ticket(Guid typeId, SupportTicketStatus status, DateTimeOffset? resolvedAt, DateOnly? dueOn) =>
        new(Guid.CreateVersion7(), typeId, status, Friday, resolvedAt, dueOn);
}
