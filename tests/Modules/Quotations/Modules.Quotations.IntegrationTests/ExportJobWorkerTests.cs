using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Exports;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El hosted service de verdad (D7): que esté registrado y que tome un job sin que nadie lo
/// llame. Es la única prueba que espera un tick; el resto corre el runner (o el drenado) a mano.
/// </summary>
public sealed class ExportJobWorkerTests
{
    [Fact]
    public async Task TheHostedWorkerCompletesAPendingJobOnItsOwn()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString(), runExportWorker: true);
        using var factory = baseFactory.WithExportProcessors(
            new SucceedingExportProcessor(ExportJobKind.Quotations));

        var jobId = await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Equal(ExportJobStatus.Completed, await WaitForStatusAsync(factory, jobId, ExportJobStatus.Completed));
    }

    // El interruptor del harness: sin él, cada prueba de la cola competiría con el worker.
    [Fact]
    public async Task TheTestHostLeavesTheWorkerOutByDefault()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        Assert.Empty(factory.Services.GetServices<IHostedService>().OfType<ExportJobWorker>());
    }

    // Fix round 1 (revisión de Task 5): la prueba anterior llamaba dos veces a RunExportJobAsync,
    // que ya abría un scope nuevo por llamada desde antes de Task 5 (ver QuotationsApiHarness.cs)
    // -- no ejercitaba en absoluto el `while (true)` de ExportJobWorker.DrainAsync, así que no
    // habría detectado una regresión ahí. Esta llama directamente a DrainAsync (expuesto
    // `internal` sólo para esto, ver el comentario en ExportJobWorker.cs) sobre una instancia real
    // del worker, con dos jobs pendientes para el MISMO drenado: el primero pierde el lease a
    // mitad de proceso (misma carrera determinística que
    // ExportJobRunnerIntegrationTests.LeaseLostDuringProcessingPublishesNothing, Task 4) y el
    // segundo, de otro kind, tiene que completar limpio en esa misma llamada. Si alguien sacara el
    // `CreateAsyncScope()` de adentro del `while` (un solo scope para todo el drenado), el
    // DbContext seguiría trackeando al primer job como Modified con un token de concurrencia
    // vencido, y el SaveChanges del segundo se caería arrastrado por esa entidad ajena -- sin
    // esperar ningún tick real, porque acá DrainAsync se llama una sola vez.
    [Fact]
    public async Task DrainAsyncGivesEachJobItsOwnScopeSoALostLeaseDoesNotPoisonTheNextJob()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        var racedProcessor = new CallbackExportProcessor(ExportJobKind.Quotations);
        using var factory = baseFactory.WithExportProcessors(
            racedProcessor, new SucceedingExportProcessor(ExportJobKind.Sales));

        var racedJobId = await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        var cleanJobId = await EnqueueExportJobAsync(
            factory, Guid.CreateVersion7(), Guid.CreateVersion7(), ExportJobKind.Sales);

        // Se fija recién acá, con la factory ya construida, para poder cerrar sobre ella.
        racedProcessor.OnProcessing = async (job, cancellationToken) =>
        {
            await ExpireExportLeaseAsync(factory, job.Id);
            await using var raceScope = factory.Services.CreateAsyncScope();
            var reclaimed = await raceScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
                .ClaimNextAsync(DateTimeOffset.UtcNow, cancellationToken);
            Assert.NotNull(reclaimed);
            Assert.Equal(job.Id, reclaimed.Id);
        };

        // La misma clase que registra el DI, construida a mano: mismo constructor, sin pasar por
        // el hosting ni por el PeriodicTimer real de 5 s.
        var worker = new ExportJobWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<ExportJobWorker>>());

        await worker.DrainAsync(TestContext.Current.CancellationToken);

        var cleanJob = await FindExportJobAsync(factory, cleanJobId);
        Assert.Equal(ExportJobStatus.Completed, cleanJob.Status);
        // El job raceado queda Processing, con la carrera dueña de su lease -- este drenado no lo toca.
        var racedJob = await FindExportJobAsync(factory, racedJobId);
        Assert.Equal(ExportJobStatus.Processing, racedJob.Status);

        var ready = Assert.Single(await OutboxMessagesAsync(factory, "quotations.export-ready.v1"));
        Assert.Equal(cleanJobId.ToString("D", CultureInfo.InvariantCulture), ready.CorrelationId);

        var audits = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        Assert.DoesNotContain(audits, audit =>
        {
            using var entry = JsonDocument.Parse(audit.PayloadJson);
            return entry.RootElement.GetProperty("resourceId").GetString()
                == racedJobId.ToString("D", CultureInfo.InvariantCulture);
        });
    }

    // La medición de la exportación (spec 2026-09-13) lee la duración de cada job en el log del pod.
    [Fact]
    public async Task DrainAsyncLogsHowLongEachJobTook()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = baseFactory.WithExportProcessors(
            new SucceedingExportProcessor(ExportJobKind.Quotations));
        await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        var logger = new RecordingLogger<ExportJobWorker>();

        await new ExportJobWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), logger)
            .DrainAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries, candidate => candidate.Level == LogLevel.Information);
        Assert.Equal("Completed", entry.State["Outcome"]?.ToString());
        Assert.IsType<long>(entry.State["ElapsedMilliseconds"]);
    }

    // Mismo mecanismo que InvitationNotificationTests: sondeo con plazo, porque el tick es del
    // worker y no de la prueba.
    private static async Task<ExportJobStatus?> WaitForStatusAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        Guid jobId,
        ExportJobStatus expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        ExportJobStatus? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = (await FindExportJobAsync(factory, jobId)).Status;
            if (last == expected)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return last;
    }
}
