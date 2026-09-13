using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El runner contra la base: que cerrar un job deje estado, evento y auditoría juntos (D10), y
/// que los reintentos terminen en Failed con su evento (D11). Los procesadores son de mentira;
/// el Excel lo cubren las pruebas de los endpoints.
/// </summary>
public sealed class ExportJobRunnerIntegrationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid RequesterId = Guid.CreateVersion7();

    [Fact]
    public async Task CompletingAJobWritesTheStatusTheReadyEventAndTheAuditTogether()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithExportProcessors(
            new SucceedingExportProcessor(ExportJobKind.Quotations));
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        var outcome = await RunExportJobAsync(factory);

        Assert.Equal(ExportJobRunOutcome.Completed, outcome);
        var job = await FindExportJobAsync(factory, jobId);
        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal(3, job.RowCount);
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", job.FileName);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.LockedUntil);

        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        using var payload = JsonDocument.Parse(ready.PayloadJson);
        Assert.Equal(TenantId, payload.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(RequesterId, payload.RootElement.GetProperty("subjectId").GetGuid());
        Assert.Equal("Quotations", payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", payload.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("rowCount").GetInt32());
        Assert.StartsWith("https://r2.test/exports/", payload.RootElement.GetProperty("downloadUrl").GetString(), StringComparison.Ordinal);
        Assert.True(payload.RootElement.TryGetProperty("expiresAt", out _));
        Assert.Equal(jobId.ToString(), ready.CorrelationId);

        var audits = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        Assert.Contains(audits, audit =>
        {
            using var entry = JsonDocument.Parse(audit.PayloadJson);
            return entry.RootElement.GetProperty("action").GetString() == "quotation.quotation.exported"
                && entry.RootElement.GetProperty("resourceId").GetString() == jobId.ToString()
                && entry.RootElement.GetProperty("outcome").GetString() == "success:3";
        });
        Assert.Empty(await OutboxMessagesAsync(factory, "quotations.export-failed.v1"));
    }

    [Fact]
    public async Task RetriesEndInFailedWithASingleFailedEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithExportProcessors(
            new FailingExportProcessor(ExportJobKind.Quotations, new IOException("r2 unavailable")));
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        // Tres reintentos —las esperas de 1, 5 y 15 minutos, adelantadas en la base— sin correo,
        // y el cuarto intento cierra el job.
        for (var attempt = 1; attempt < ExportJob.MaxAttempts; attempt++)
        {
            Assert.Equal(ExportJobRunOutcome.RetryScheduled, await RunExportJobAsync(factory));
            Assert.Empty(await OutboxMessagesAsync(factory, "quotations.export-failed.v1"));
            await MakeExportJobDueAsync(factory, jobId);
        }

        Assert.Equal(ExportJobRunOutcome.Failed, await RunExportJobAsync(factory));

        var job = await FindExportJobAsync(factory, jobId);
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(4, job.Attempts);
        Assert.Equal("IOException: r2 unavailable", job.LastError);
        var failed = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-failed.v1"));
        using var payload = JsonDocument.Parse(failed.PayloadJson);
        Assert.Equal(RequesterId, payload.RootElement.GetProperty("subjectId").GetGuid());
        Assert.Equal("Quotations", payload.RootElement.GetProperty("kind").GetString());
        Assert.Empty(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
    }

    // Fix round 1 (revisión de Task 4): la garantía de "nada se publica si se pierde el lease"
    // sólo estaba probada contra dobles en memoria (ExportJobRunnerTests, Task 3), donde el
    // guardado nunca toca Postgres de verdad. Acá se fuerza la carrera de forma determinística
    // —sin ningún sleep— desde dentro del propio procesador: mientras el runner todavía tiene el
    // job de su primera toma (Attempts = 1) trackeado en memoria, la carrera vence el lease y lo
    // vuelve a tomar en un scope aparte (Attempts pasa a 2, commiteado ya). Cuando el runner
    // completa el job y guarda, `attempts` como token de concurrencia hace que el UPDATE no
    // encuentre la fila: RequestConcurrencyException, LeaseLost, y el guardado entero —job, evento
    // y auditoría— se cae en el mismo commit. Nada de esto depende de ganar una carrera de
    // temporización real.
    [Fact]
    public async Task LeaseLostDuringProcessingPublishesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        var processor = new CallbackExportProcessor(ExportJobKind.Quotations);
        using var factory = baseFactory.WithExportProcessors(processor);
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        // Se fija recién acá, con la factory ya construida, para poder cerrar sobre ella.
        processor.OnProcessing = async (job, cancellationToken) =>
        {
            await ExpireExportLeaseAsync(factory, job.Id);
            await using var raceScope = factory.Services.CreateAsyncScope();
            var reclaimed = await raceScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
                .ClaimNextAsync(DateTimeOffset.UtcNow, cancellationToken);
            Assert.NotNull(reclaimed);
            Assert.Equal(job.Id, reclaimed.Id);
        };

        var outcome = await RunExportJobAsync(factory);

        Assert.Equal(ExportJobRunOutcome.LeaseLost, outcome);
        Assert.Empty(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        var audits = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        Assert.DoesNotContain(audits, audit =>
        {
            using var entry = JsonDocument.Parse(audit.PayloadJson);
            return entry.RootElement.GetProperty("resourceId").GetString() == jobId.ToString();
        });
        var job = await FindExportJobAsync(factory, jobId);
        Assert.Equal(ExportJobStatus.Processing, job.Status);
        Assert.Equal(2, job.Attempts);
    }
}
