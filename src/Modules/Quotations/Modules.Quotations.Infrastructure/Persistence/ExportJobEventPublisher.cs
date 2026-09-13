using System.Globalization;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

// Acumula los avisos en la proyección de outbox de QuotationsDbContext, para que commiteen en la
// misma transacción que el cambio de estado del job (D10). Los consumen los dos workers de
// Notifications, que resuelven el correo del solicitante. Mismo mecanismo que
// CustomerExportEventPublisher.
internal sealed class ExportJobEventPublisher(QuotationsDbContext dbContext) : IExportEventPublisher
{
    internal const string ReadyEventName = "quotations.export-ready.v1";
    internal const string FailedEventName = "quotations.export-failed.v1";

    public void PublishReady(ExportJob job, ExportJobResult result, DateTimeOffset occurredAt) =>
        Add(
            job,
            ReadyEventName,
            JsonSerializer.Serialize(new ExportReadyPayload(
                job.TenantId,
                job.RequestedBy,
                job.Kind.ToString(),
                result.DownloadUrl,
                result.FileName,
                result.RowCount,
                result.ExpiresAt)),
            occurredAt);

    public void PublishFailed(ExportJob job, DateTimeOffset occurredAt) =>
        Add(
            job,
            FailedEventName,
            JsonSerializer.Serialize(new ExportFailedPayload(
                job.TenantId, job.RequestedBy, job.Kind.ToString())),
            occurredAt);

    // La correlación es el id del job: soporte llega del correo a la fila de export_jobs sin
    // adivinar cuál de las de ese minuto era.
    private void Add(ExportJob job, string eventName, string payload, DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new QuotationsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = eventName,
            PayloadJson = payload,
            CorrelationId = job.Id.ToString("D", CultureInfo.InvariantCulture),
            OccurredAt = occurredAt,
        });

    // Nombres en minúscula como el resto de los payloads del outbox: el consumidor los lee por
    // nombre con JsonDocument.
    private sealed record ExportReadyPayload(
        Guid tenantId,
        Guid subjectId,
        string kind,
        string downloadUrl,
        string fileName,
        int rowCount,
        DateTimeOffset expiresAt);

    private sealed record ExportFailedPayload(Guid tenantId, Guid subjectId, string kind);
}
