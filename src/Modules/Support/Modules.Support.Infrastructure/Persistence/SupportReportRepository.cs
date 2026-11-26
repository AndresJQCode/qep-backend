using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>Fase 4b (H24): las dos proyecciones del reporte, sin tracking: los tickets radicados en el rango y las encuestas de esos
/// tickets. Sólo tablas de Soporte.</summary>
internal sealed class SupportReportRepository(SupportDbContext dbContext) : ISupportReportRepository
{
    public async Task<IReadOnlyList<SupportReportTicketRow>> ListTicketsAsync(
        Guid tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var (from, to) = (fromUtc.ToUniversalTime(), toUtc.ToUniversalTime());
        // Revisión del bloque D: el último cambio de estado de cada ticket, de la traza, para saber si un Cerrado viene de Resuelto.
        var rows = await dbContext.Tickets
            .AsNoTracking()
            // Spec 2026-11-22 §5.3: una tarea interna no es un ticket de cliente; fuera de la fuente, así los totales y el desglose por
            // tipo (que ya omite el tipo interno) cuentan lo mismo.
            .Where(ticket => ticket.TenantId == tenantId && !ticket.IsInternal && ticket.ReceivedAt >= from && ticket.ReceivedAt < to)
            .Select(ticket => new
            {
                ticket.Id,
                ticket.TypeId,
                ticket.Status,
                ticket.ReceivedAt,
                ticket.ResolvedAt,
                ticket.DueOn,
                LastStatusChange = dbContext.TicketEvents
                    .Where(item => item.TenantId == tenantId && item.TicketId == ticket.Id && item.Kind == SupportTicketEventKind.StatusChanged)
                    .OrderByDescending(item => item.Ordinal)
                    .Select(item => item.Details)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return rows
            .Select(row => new SupportReportTicketRow(
                row.Id.Value, row.TypeId.Value, row.Status, row.ReceivedAt, row.ResolvedAt, row.DueOn,
                row.Status is SupportTicketStatus.Closed && FromOf(row.LastStatusChange) == nameof(SupportTicketStatus.Resolved)))
            .ToList();
    }

    // Los detalles de StatusChanged son { "from": "...", "to": "..." } (SupportTicket.ChangeStatus).
    private static string? FromOf(string? details)
    {
        if (string.IsNullOrEmpty(details))
        {
            return null;
        }

        using var document = JsonDocument.Parse(details);
        return document.RootElement.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String ? from.GetString() : null;
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
                    && !ticket.IsInternal
                    && ticket.ReceivedAt >= from
                    && ticket.ReceivedAt < to))
            .Select(survey => new SupportReportSurveyRow(survey.TicketId.Value, survey.Status, survey.ReasonCode, survey.Rating))
            .ToListAsync(cancellationToken);
    }
}
