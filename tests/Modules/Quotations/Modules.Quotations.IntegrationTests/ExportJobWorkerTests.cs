using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Exports;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El hosted service de verdad (D7): que esté registrado y que tome un job sin que nadie lo
/// llame. Es la única prueba que espera un tick; el resto corre el runner a mano.
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

    // Fix (revisión de Task 5): `DrainAsync` abre un scope nuevo por llamada a `RunNextAsync`
    // —igual que `RunExportJobAsync` acá abajo—, así que un job que pierde el lease a mitad de
    // proceso no deja nada trackeado que contamine el siguiente. Se fuerza la pérdida de lease
    // sobre un primer job (misma carrera determinística que
    // `ExportJobRunnerIntegrationTests.LeaseLostDuringProcessingPublishesNothing`, Task 4) y
    // después se corre un segundo job de otro `kind`, que sólo puede completar en un scope
    // limpio: si `DrainAsync` reusara el scope del job perdido, la entidad ya trackeada del
    // primer job seguiría ahí cuando este segundo intente guardar.
    [Fact]
    public async Task AJobProcessedAfterALostLeaseOnAnotherJobStartsClean()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        var racedProcessor = new CallbackExportProcessor(ExportJobKind.Quotations);
        using var factory = baseFactory.WithExportProcessors(
            racedProcessor, new SucceedingExportProcessor(ExportJobKind.Sales));

        var racedJobId = await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), Guid.CreateVersion7());

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

        var lost = await RunExportJobAsync(factory);
        Assert.Equal(ExportJobRunOutcome.LeaseLost, lost);

        var cleanJobId = await EnqueueExportJobAsync(
            factory, Guid.CreateVersion7(), Guid.CreateVersion7(), ExportJobKind.Sales);

        var completed = await RunExportJobAsync(factory);

        Assert.Equal(ExportJobRunOutcome.Completed, completed);
        var cleanJob = await FindExportJobAsync(factory, cleanJobId);
        Assert.Equal(ExportJobStatus.Completed, cleanJob.Status);
        // El job raceado queda Processing, con la carrera dueña de su lease -- no lo toca este job.
        var racedJob = await FindExportJobAsync(factory, racedJobId);
        Assert.Equal(ExportJobStatus.Processing, racedJob.Status);
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
