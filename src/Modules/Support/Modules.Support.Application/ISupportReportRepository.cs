using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H25): lo que el reporte necesita de un ticket de la población.</summary>
public sealed record SupportReportTicketRow(
    Guid TicketId, Guid TypeId, SupportTicketStatus Status, DateTimeOffset ReceivedAt, DateTimeOffset? ResolvedAt, DateOnly? DueOn);

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
