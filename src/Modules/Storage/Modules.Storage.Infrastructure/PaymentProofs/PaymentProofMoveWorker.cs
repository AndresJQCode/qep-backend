using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Storage.Infrastructure.PaymentProofs;

// Spec 2026-09-16, D9. Cada 3 s, como los demás consumidores del outbox: la sección 2 del spec cuenta
// con que entre guardar el pedido y borrar el temporal pasen segundos. Cada tick corre en su propio
// scope para que el DbContext esté fresco.
internal sealed partial class PaymentProofMoveWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentProofMoveWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof move tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPaymentProofMoveProcessor>();
                await processor.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
