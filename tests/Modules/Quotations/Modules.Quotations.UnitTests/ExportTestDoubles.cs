using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Dobles de la exportación asíncrona. Como el resto del repositorio, a mano y sin librería de
// mocking: registran lo que reciben para que la prueba afirme qué salió hacia cada puerto.

/// <summary>La cola sin Postgres: toma con <see cref="ExportJob.Claim"/>, que es la misma
/// transición que el UPDATE con SKIP LOCKED. La exclusión real la cubren las pruebas de
/// integración de ExportJobQueue.</summary>
internal sealed class InMemoryExportJobQueue : IExportJobQueue
{
    public List<ExportJob> Jobs { get; } = [];

    public DateTimeOffset? LastPurgeCutoff { get; private set; }

    public void Add(ExportJob job) => Jobs.Add(job);

    public Task<int> CountPendingAsync(
        Guid tenantId, Guid requestedBy, CancellationToken cancellationToken) =>
        Task.FromResult(Jobs.Count(job =>
            job.TenantId == tenantId
            && job.RequestedBy == requestedBy
            && job.Status is ExportJobStatus.Pending or ExportJobStatus.Processing));

    public Task<ExportJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = Jobs
            .Where(candidate => candidate.IsClaimable(now))
            .OrderBy(candidate => candidate.NextAttemptAt)
            .FirstOrDefault();
        job?.Claim(now);
        return Task.FromResult(job);
    }

    public Task<int> PurgeFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        LastPurgeCutoff = cutoff;
        return Task.FromResult(0);
    }
}

/// <summary>Cuenta los guardados: "terminar es una sola transacción" (D10) se afirma como
/// exactamente un guardado. <see cref="Failure"/> simula que el guardado explota.</summary>
internal sealed class CountingQuotationsUnitOfWork : IQuotationsUnitOfWork
{
    public int Saves { get; private set; }

    public Exception? Failure { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException<int>(Failure);
        }

        Saves++;
        return Task.FromResult(1);
    }
}

internal sealed class RecordingExportEventPublisher : IExportEventPublisher
{
    public List<(ExportJob Job, ExportJobResult Result)> Ready { get; } = [];

    public List<ExportJob> Failed { get; } = [];

    public void PublishReady(ExportJob job, ExportJobResult result, DateTimeOffset occurredAt) =>
        Ready.Add((job, result));

    public void PublishFailed(ExportJob job, DateTimeOffset occurredAt) => Failed.Add(job);
}

internal sealed record RecordedAuditEntry(
    Guid TenantId, Guid ActorId, string Action, string ResourceId, string Outcome);

internal sealed class RecordingExportAuditPublisher : IQuotationAuditPublisher
{
    public List<RecordedAuditEntry> Entries { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt) =>
        Entries.Add(new RecordedAuditEntry(tenantId, actorId, action, resourceId, outcome));
}

internal sealed class StubExportJobProcessor(
    ExportJobKind kind, Func<ExportJob, ExportJobResult> process) : IExportJobProcessor
{
    public static readonly DateTimeOffset LinkExpiresAt = new(2026, 9, 13, 15, 30, 0, TimeSpan.Zero);

    public int Calls { get; private set; }

    public ExportJobKind Kind { get; } = kind;

    public Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(process(job));
    }

    public static StubExportJobProcessor Succeeding(ExportJobKind kind, int rowCount = 3) =>
        new(kind, _ => new ExportJobResult(
            "cotizaciones-2026-09-12-1530.xlsx", rowCount, "https://r2.test/exports/x.xlsx", LinkExpiresAt));

    public static StubExportJobProcessor Throwing(ExportJobKind kind, Exception failure) =>
        new(kind, _ => throw failure);
}

/// <summary>Un reloj que la prueba adelanta: el backoff se afirma en minutos exactos.</summary>
internal sealed class MutableClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
