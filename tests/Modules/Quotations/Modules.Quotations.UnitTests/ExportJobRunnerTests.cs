using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Un tick del worker de exportaciones: toma un job, lo despacha por kind y lo cierra. Lo que
/// verifican estas pruebas es la clasificación de fallos (D11) y que terminar sea un solo
/// guardado con estado, evento y auditoría (D10).
/// </summary>
public sealed class ExportJobRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid RequesterId = Guid.CreateVersion7();

    [Fact]
    public async Task WithNothingDueItReportsNoJob()
    {
        var harness = new Harness();

        var outcome = await harness.Runner().RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.NoJob, outcome);
        Assert.Equal(0, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ACompletedJobPublishesTheReadyEventAndTheAuditInOneSave()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var processor = StubExportJobProcessor.Succeeding(ExportJobKind.Quotations, rowCount: 42);

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Completed, outcome);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(42, job.RowCount);
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", job.FileName);
        var (readyJob, result) = Assert.Single(harness.Events.Ready);
        Assert.Same(job, readyJob);
        Assert.Equal("https://r2.test/exports/x.xlsx", result.DownloadUrl);
        Assert.Empty(harness.Events.Failed);
        Assert.Equal(
            new RecordedAuditEntry(
                TenantId, RequesterId, "quotation.quotation.exported", job.Id.ToString(), "success:42"),
            Assert.Single(harness.Audit.Entries));
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AOrdersJobIsAuditedAsAOrderExport()
    {
        var harness = new Harness();
        harness.Enqueue(ExportJobKind.Sales);

        await harness.Runner(StubExportJobProcessor.Succeeding(ExportJobKind.Sales))
            .RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal("quotation.sale.exported", Assert.Single(harness.Audit.Entries).Action);
    }

    // R2 caído, la base, un timeout: puede no repetirse, así que vuelve a la cola sin correo.
    [Fact]
    public async Task ATransientFailureSchedulesARetryWithoutAnEvent()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var processor = StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new IOException("r2 unavailable"));

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.RetryScheduled, outcome);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAt);
        Assert.Equal("IOException: r2 unavailable", job.LastError);
        Assert.Empty(harness.Events.Failed);
        Assert.Empty(harness.Audit.Entries);
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    // D11: esperas de 1, 5 y 15 minutos, sin correo mientras quede un intento; el cuarto fallido
    // cierra el job y recién ahí sale el evento.
    [Fact]
    public async Task TheFourthTransientFailureFailsTheJobAndPublishesTheFailedEvent()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var runner = harness.Runner(StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new IOException("r2 unavailable")));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(ExportJobRunOutcome.RetryScheduled, await runner.RunNextAsync(cancellationToken));
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAt);
        harness.Clock.UtcNow = Now.AddMinutes(1);
        Assert.Equal(ExportJobRunOutcome.RetryScheduled, await runner.RunNextAsync(cancellationToken));
        Assert.Equal(Now.AddMinutes(6), job.NextAttemptAt);
        harness.Clock.UtcNow = Now.AddMinutes(6);
        Assert.Equal(ExportJobRunOutcome.RetryScheduled, await runner.RunNextAsync(cancellationToken));
        Assert.Equal(Now.AddMinutes(21), job.NextAttemptAt);
        Assert.Empty(harness.Events.Failed);
        harness.Clock.UtcNow = Now.AddMinutes(21);
        var outcome = await runner.RunNextAsync(cancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(4, job.Attempts);
        Assert.Same(job, Assert.Single(harness.Events.Failed));
    }

    // Filtros ilegibles o cero filas: reintentar da lo mismo, así que Failed al primer intento.
    [Fact]
    public async Task ADefinitiveFailureFailsOnTheFirstAttempt()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        var processor = StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new ExportJobDefinitiveException("no rows"));

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("ExportJobDefinitiveException: no rows", job.LastError);
        Assert.Same(job, Assert.Single(harness.Events.Failed));
    }

    [Fact]
    public async Task AKindWithoutProcessorFailsDefinitively()
    {
        var harness = new Harness();
        var job = harness.Enqueue(ExportJobKind.Sales);

        var outcome = await harness.Runner(StubExportJobProcessor.Succeeding(ExportJobKind.Quotations))
            .RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.StartsWith("NoProcessor:", job.LastError, StringComparison.Ordinal);
    }

    // El worker murió en el cuarto intento y el lease venció: la toma suma un quinto y el job se
    // cierra sin procesar, o un job colgado consumiría intentos para siempre.
    [Fact]
    public async Task AJobReclaimedPastItsLastAttemptFailsWithoutProcessing()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        job.Claim(Now);
        job.Claim(Now.AddMinutes(11));
        job.Claim(Now.AddMinutes(22));
        job.Claim(Now.AddMinutes(33));
        harness.Clock.UtcNow = Now.AddMinutes(44);
        var processor = StubExportJobProcessor.Succeeding(ExportJobKind.Quotations);

        var outcome = await harness.Runner(processor).RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.Failed, outcome);
        Assert.Equal(0, processor.Calls);
        Assert.StartsWith("LeaseExpired:", job.LastError, StringComparison.Ordinal);
        Assert.Same(job, Assert.Single(harness.Events.Failed));
    }

    // Otro worker retomó el job mientras éste terminaba: el UPDATE con el token de concurrencia
    // no encuentra la fila, y lo correcto es soltarlo sin tirar el tick.
    [Fact]
    public async Task ALostLeaseOnCompletionIsReportedAndNotThrown()
    {
        var harness = new Harness();
        harness.Enqueue();
        harness.UnitOfWork.Failure = new RequestConcurrencyException("concurrency.conflict", "lease lost");

        var outcome = await harness.Runner(StubExportJobProcessor.Succeeding(ExportJobKind.Quotations))
            .RunNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExportJobRunOutcome.LeaseLost, outcome);
    }

    // D10: si el guardado final falla por otra cosa que el lease, la excepción sube al worker y
    // el scope se descarta. Convertirla en un reintento sobre el mismo contexto guardaría el evento
    // de "listo" y la auditoría ya preparados junto con un Pending: un correo con el enlace y,
    // después del reintento, otro.
    [Fact]
    public async Task AFailedCompletionSaveThatIsNotALostLeasePropagates()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        harness.UnitOfWork.Failure = new IOException("disk");

        var error = await Assert.ThrowsAsync<IOException>(
            () => harness.Runner(StubExportJobProcessor.Succeeding(ExportJobKind.Quotations))
                .RunNextAsync(TestContext.Current.CancellationToken));

        Assert.Equal("disk", error.Message);
        Assert.Equal(0, harness.UnitOfWork.Saves);
        Assert.Null(job.LastError);
        Assert.Empty(harness.Events.Failed);
    }

    // Apagado del proceso: no es un fallo del job. Queda en Processing y el lease lo devuelve.
    [Fact]
    public async Task CancellationDuringProcessingLeavesTheJobClaimed()
    {
        var harness = new Harness();
        var job = harness.Enqueue();
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var processor = StubExportJobProcessor.Throwing(
            ExportJobKind.Quotations, new OperationCanceledException(shutdown.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Runner(processor).RunNextAsync(shutdown.Token));

        Assert.Equal(ExportJobStatus.Processing, job.Status);
        Assert.Equal(0, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task PurgeRemovesWhatFinishedMoreThanThirtyDaysAgo()
    {
        var harness = new Harness();

        await harness.Runner().PurgeFinishedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Now.AddDays(-30), harness.Queue.LastPurgeCutoff);
    }

    private sealed class Harness
    {
        public InMemoryExportJobQueue Queue { get; } = new();

        public RecordingExportEventPublisher Events { get; } = new();

        public RecordingExportAuditPublisher Audit { get; } = new();

        public CountingQuotationsUnitOfWork UnitOfWork { get; } = new();

        public MutableClock Clock { get; } = new(Now);

        public ExportJob Enqueue(ExportJobKind kind = ExportJobKind.Quotations)
        {
            var job = ExportJob.Enqueue(Guid.CreateVersion7(), TenantId, RequesterId, kind, "{}", Now);
            Queue.Add(job);
            return job;
        }

        public ExportJobRunner Runner(params IExportJobProcessor[] processors) =>
            new(Queue, processors, Events, Audit, UnitOfWork, Clock);
    }
}
