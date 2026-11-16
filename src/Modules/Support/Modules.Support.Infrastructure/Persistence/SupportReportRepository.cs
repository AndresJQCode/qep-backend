using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>Fase 4b (H24): las dos proyecciones del reporte, sin tracking: los tickets radicados en el rango y las encuestas de esos
/// tickets. Sólo tablas de Soporte.</summary>
internal sealed class SupportReportRepository(SupportDbContext dbContext) : ISupportReportRepository
{
    public async Task<IReadOnlyList<SupportReportTicketRow>> ListTicketsAsync(
        Guid tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var (from, to) = (fromUtc.ToUniversalTime(), toUtc.ToUniversalTime());
        return await dbContext.Tickets
            .AsNoTracking()
            .Where(ticket => ticket.TenantId == tenantId && ticket.ReceivedAt >= from && ticket.ReceivedAt < to)
            .Select(ticket => new SupportReportTicketRow(
                ticket.Id.Value, ticket.TypeId.Value, ticket.Status, ticket.ReceivedAt, ticket.ResolvedAt, ticket.DueOn))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SupportReportSurveyRow>> ListSurveysAsync(
        Guid tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var (from, to) = (fromUtc.ToUniversalTime(), toUtc.ToUniversalTime());
        return await dbContext.TicketSurveys
            .AsNoTracking()
            .Where(survey => survey.TenantId == tenantId
                && dbContext.Tickets.Any(ticket => ticket.Id == survey.TicketId
                    && ticket.TenantId == tenantId
                    && ticket.ReceivedAt >= from
                    && ticket.ReceivedAt < to))
            .Select(survey => new SupportReportSurveyRow(survey.TicketId.Value, survey.Status, survey.ReasonCode, survey.Rating))
            .ToListAsync(cancellationToken);
    }
}
