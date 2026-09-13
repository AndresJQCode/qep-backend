using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La cola de exportaciones contra Postgres real (D6): la toma con SKIP LOCKED, el lease, el
/// conteo del límite de pendientes y la purga. Nada de esto se puede probar con un doble: la
/// exclusión la da la base.
/// </summary>
public sealed class ExportJobQueueTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid RequesterId = Guid.CreateVersion7();

    [Fact]
    public async Task ClaimTakesADueJobWithATenMinuteLeaseAndConsumesAnAttempt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);
        var now = DateTimeOffset.UtcNow;

        await using var scope = factory.Services.CreateAsyncScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(now, TestContext.Current.CancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed.Id);
        var stored = await FindExportJobAsync(factory, jobId);
        Assert.Equal(ExportJobStatus.Processing, stored.Status);
        Assert.Equal(1, stored.Attempts);
        // Postgres guarda microsegundos y DateTimeOffset tiene ticks de 100 ns.
        Assert.Equal(now.AddMinutes(10), stored.LockedUntil!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ClaimSkipsAJobWaitingItsBackoff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var scope = factory.Services.CreateAsyncScope();
        var claimed = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow.AddMinutes(-1), TestContext.Current.CancellationToken);

        Assert.Null(claimed);
    }

    // La toma de A queda sin commitear, con la fila bloqueada: B tiene que saltarla y llevarse
    // la otra, no esperar ni tomar la misma. Es lo que hace que escalar a más réplicas no genere
    // el mismo export dos veces.
    [Fact]
    public async Task TwoConcurrentClaimsNeverTakeTheSameJob()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var first = await EnqueueExportJobAsync(factory, TenantId, RequesterId);
        var second = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var holderScope = factory.Services.CreateAsyncScope();
        var holderContext = holderScope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await using var holding = await holderContext.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var claimedByA = await holderScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        await using var otherScope = factory.Services.CreateAsyncScope();
        using var notBlocked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        notBlocked.CancelAfter(TimeSpan.FromSeconds(5));
        var claimedByB = await otherScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, notBlocked.Token);

        Assert.NotNull(claimedByA);
        Assert.NotNull(claimedByB);
        Assert.Equal(
            new HashSet<Guid> { first, second },
            new HashSet<Guid> { claimedByA.Id, claimedByB.Id });
        await holding.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AJobLockedByAnotherTransactionIsSkippedWithoutWaiting()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var holderScope = factory.Services.CreateAsyncScope();
        var holderContext = holderScope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        await using var holding = await holderContext.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(await holderScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        await using var otherScope = factory.Services.CreateAsyncScope();
        using var notBlocked = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        notBlocked.CancelAfter(TimeSpan.FromSeconds(5));
        var claimedByB = await otherScope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, notBlocked.Token);

        Assert.Null(claimedByB);
        await holding.RollbackAsync(TestContext.Current.CancellationToken);
    }

    // Worker muerto a mitad (D11): el lease vence y el siguiente tick lo retoma. Con el lease
    // vivo, nadie más lo toca.
    [Fact]
    public async Task AnExpiredLeaseIsReclaimedAndALiveOneIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var jobId = await EnqueueExportJobAsync(factory, TenantId, RequesterId);
        await ClaimAsync(factory);
        await ExpireExportLeaseAsync(factory, jobId);

        var reclaimed = await ClaimAsync(factory);
        var again = await ClaimAsync(factory);

        Assert.NotNull(reclaimed);
        Assert.Equal(jobId, reclaimed.Id);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.Null(again);
    }

    [Fact]
    public async Task CountPendingCountsBothKindsOnlyForThatRequester()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await EnqueueExportJobAsync(factory, TenantId, RequesterId, ExportJobKind.Quotations);
        await EnqueueExportJobAsync(factory, TenantId, RequesterId, ExportJobKind.Sales);
        await EnqueueExportJobAsync(factory, TenantId, Guid.CreateVersion7(), ExportJobKind.Sales);
        await EnqueueExportJobAsync(factory, Guid.CreateVersion7(), RequesterId, ExportJobKind.Sales);
        await ClaimAsync(factory); // uno pasa a Processing: sigue contando
        await InsertFinishedAsync(factory, DateTimeOffset.UtcNow); // terminado: no cuenta

        await using var scope = factory.Services.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .CountPendingAsync(TenantId, RequesterId, TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
    }

    // D13: lo terminado hace más de 30 días se va; lo reciente y lo vivo se quedan.
    [Fact]
    public async Task PurgeDeletesOnlyFinishedJobsOlderThanTheCutoff()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var now = DateTimeOffset.UtcNow;
        var old = await InsertFinishedAsync(factory, now.AddDays(-31));
        var recent = await InsertFinishedAsync(factory, now.AddDays(-29));
        var pending = await EnqueueExportJobAsync(factory, TenantId, RequesterId);

        await using var scope = factory.Services.CreateAsyncScope();
        var purged = await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .PurgeFinishedBeforeAsync(now.AddDays(-30), TestContext.Current.CancellationToken);

        Assert.Equal(1, purged);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FindExportJobAsync(factory, old));
        Assert.Equal(ExportJobStatus.Completed, (await FindExportJobAsync(factory, recent)).Status);
        Assert.Equal(ExportJobStatus.Pending, (await FindExportJobAsync(factory, pending)).Status);
    }

    private static async Task<ExportJob?> ClaimAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IExportJobQueue>()
            .ClaimNextAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    }

    // Un job ya terminado en una fecha dada: se arma en memoria con las transiciones del dominio
    // y se guarda así, porque la API no deja fabricar un completed_at en el pasado.
    private static async Task<Guid> InsertFinishedAsync(QepApiFactory factory, DateTimeOffset finishedAt)
    {
        var job = ExportJob.Enqueue(
            Guid.CreateVersion7(), TenantId, RequesterId, ExportJobKind.Quotations, "{}", finishedAt);
        job.Claim(finishedAt);
        job.Complete("cotizaciones-vieja.xlsx", 1, finishedAt);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IExportJobQueue>().Add(job);
        await scope.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return job.Id;
    }
}
