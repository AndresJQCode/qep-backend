using System.Text.Json;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

// Mismo mecanismo que CatalogAuditPublisher: el proyector del outbox lo escribe despues en
// audit.entries, dueño del modulo Audit. Esta escritura solo agrega la fila del Outbox en la
// misma transaccion que el cambio de negocio.
internal sealed class QuotationAuditPublisher(QuotationsDbContext dbContext) : IQuotationAuditPublisher
{
    private const string EventName = "platform.audit.recorded.v1";

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt)
    {
        var payload = JsonSerializer.Serialize(new AuditEventPayload(
            tenantId, actorId, "Human", action, "quotation", resourceId, outcome, [], "quotations", occurredAt));
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = payload,
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
        });
    }

    private sealed record AuditEventPayload(
        Guid tenantId, Guid actorId, string actorType, string action, string resourceType,
        string resourceId, string outcome, IReadOnlyCollection<string> changedFields,
        string source, DateTimeOffset occurredAt);
}
