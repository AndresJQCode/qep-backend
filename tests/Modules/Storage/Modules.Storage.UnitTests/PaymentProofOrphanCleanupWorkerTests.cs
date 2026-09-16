using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Storage.Infrastructure;
using Modules.Storage.Infrastructure.PaymentProofs;

namespace Modules.Storage.UnitTests;

/// <summary>
/// El calendario de la reconciliación (spec 2026-09-16, D12). Un deploy o un reinicio más seguido que
/// IntervalHours no puede impedir que corra: la primera corrida llega poco después de arrancar, no
/// después de un intervalo completo, y un apagado durante esa espera sale limpio.
/// </summary>
public sealed class PaymentProofOrphanCleanupWorkerTests
{
    [Fact]
    public void TheFirstRunWaitsMinutesNotAFullInterval()
    {
        Assert.InRange(
            PaymentProofOrphanCleanupWorker.DefaultInitialDelay, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task TheFirstRunHappensAfterTheInitialDelayWithoutWaitingIntervalHours()
    {
        var processor = new CountingProcessor();
        using var worker = NewWorker(processor, TimeSpan.FromMilliseconds(10));

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var ran = await Task.WhenAny(
            processor.FirstRun, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Same(processor.FirstRun, ran);
        Assert.Equal(1, processor.Runs);
    }

    [Fact]
    public async Task StoppingDuringTheInitialDelayExitsCleanlyWithoutRunning()
    {
        var processor = new CountingProcessor();
        using var worker = NewWorker(processor, TimeSpan.FromHours(1));

        await worker.StartAsync(TestContext.Current.CancellationToken);
        // BackgroundService arranca ExecuteAsync con Task.Run y el token de apagado: si se detiene antes
        // de que el delegado empiece, la tarea queda Canceled sin haber corrido nada. Se le da tiempo de
        // entrar en la espera inicial, que es lo que esta prueba ejerce.
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(worker.ExecuteTask);
        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask.Status);
        Assert.Equal(0, processor.Runs);
    }

    private static PaymentProofOrphanCleanupWorker NewWorker(
        CountingProcessor processor, TimeSpan initialDelay)
    {
        var services = new ServiceCollection()
            .AddScoped<IPaymentProofOrphanCleanupProcessor>(_ => processor)
            .BuildServiceProvider();
        return new PaymentProofOrphanCleanupWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new StorageOptions()),
            NullLogger<PaymentProofOrphanCleanupWorker>.Instance)
        {
            InitialDelay = initialDelay,
        };
    }

    private sealed class CountingProcessor : IPaymentProofOrphanCleanupProcessor
    {
        private readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public Task FirstRun => _firstRun.Task;

        public int Runs => _runs;

        public Task<PaymentProofOrphanCleanupResult> CleanupAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            _firstRun.TrySetResult();
            return Task.FromResult(new PaymentProofOrphanCleanupResult(0, 0, 0, 0, 0));
        }
    }
}
