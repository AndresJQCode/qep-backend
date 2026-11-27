using System.Text.Json;
using Modules.Hr.Application;

namespace Modules.Hr.Infrastructure.Persistence;

/// <summary>
/// Escribe <see cref="HrIntegrationEvents.DocumentsReleased"/> en el outbox de la plataforma con el <c>DbContext</c> de Hr: viaja en el
/// mismo <c>SaveChangesAsync</c> que el borrado del registro. No guarda.
/// </summary>
internal sealed class HrDocumentReleasePublisher(HrDbContext dbContext) : IHrDocumentReleases
{
    public void Publish(Guid tenantId, IReadOnlyCollection<Guid> fileIds, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new HrOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = HrIntegrationEvents.DocumentsReleased,
            PayloadJson = JsonSerializer.Serialize(new Payload(tenantId, [.. fileIds])),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
            Attempts = 0,
        });

    // Los nombres en camelCase son el contrato con Storage; System.Text.Json escribe los parámetros del record tal cual.
    private sealed record Payload(Guid tenantId, IReadOnlyList<Guid> fileIds);
}
