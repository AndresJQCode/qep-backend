using System.Globalization;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Copia del patrón de <c>GeographyEventPublisher</c>: cada evento es una <see cref="SupportOutboxMessage"/> del mismo
/// <see cref="SupportDbContext"/>, así que commitea o revierte con el cambio del handler. Los nombres en camelCase del payload son el
/// contrato (spec 2026-11-22 §6 D-3).
/// </summary>
internal sealed class SupportIntegrationEvents(SupportDbContext dbContext) : ISupportIntegrationEvents
{
    public void PublishTaskAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var assignee = ticket.AssigneeMemberId
            ?? throw new ArgumentException("A task-assigned event needs an assignee.", nameof(ticket));
        dbContext.OutboxMessages.Add(new SupportOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = SupportIntegrationEventNames.TaskAssigned,
            PayloadJson = JsonSerializer.Serialize(
                new TaskAssignedPayload(ticket.Id.Value, ticket.TenantId, ticket.DisplayNumber, assignee, assignedByMemberId)),
            CorrelationId = ticket.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });
    }

    // Los nombres en camelCase son el contrato: System.Text.Json serializa los records con el nombre del parámetro tal cual.
    private sealed record TaskAssignedPayload(Guid ticketId, Guid tenantId, string ticketNumber, Guid assigneeMemberId, Guid assignedByMemberId);
}
