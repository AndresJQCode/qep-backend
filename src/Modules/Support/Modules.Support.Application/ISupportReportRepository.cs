using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H25): lo que el reporte necesita de un ticket de la población. <c>ClosedFromResolved</c> (revisión del bloque D):
/// si el último cambio de estado del ticket fue de Resuelto a Cerrado. Un ticket reabierto conserva el <c>ResolvedAt</c> del ciclo
/// anterior y puede cerrarse desde «Esperando al cliente» sin resolverse otra vez: ese cierre no es una resolución.</summary>
public sealed record SupportReportTicketRow(
    Guid TicketId,
    Guid TypeId,
    SupportTicketStatus Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ResolvedAt,
    DateOnly? DueOn,
    bool ClosedFromResolved = true);

/// <summary>Fase 4b (H25): una encuesta de un ticket de la población, de cualquier ciclo de resolución.</summary>
public sealed record SupportReportSurveyRow(Guid TicketId, SupportSurveyStatus Status, string? ReasonCode, SupportSurveyRating? Rating);

/// <summary>Fase 4b (H24): las dos lecturas del reporte, sin tracking y sólo de las tablas de Soporte.</summary>
public interface ISupportReportRepository
{
    /// <summary>Los tickets del tenant radicados en <c>[<paramref name="fromUtc"/>, <paramref name="toUtc"/>)</c>.</summary>
    Task<IReadOnlyList<SupportReportTicketRow>> ListTicketsAsync(
        Guid tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);

    /// <summary>Las encuestas de esos mismos tickets.</summary>
    Task<IReadOnlyList<SupportReportSurveyRow>> ListSurveysAsync(
        Guid tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}
