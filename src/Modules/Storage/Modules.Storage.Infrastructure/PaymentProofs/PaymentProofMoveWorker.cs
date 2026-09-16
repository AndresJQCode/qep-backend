using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Storage.Infrastructure.PaymentProofs;

// Spec 2026-09-16, D9. Cada 3 s, como los demás consumidores del outbox: la sección 2 del spec cuenta
// con que entre guardar el pedido y borrar el temporal pasen segundos. Cada procesador corre en su
// propio scope para que el DbContext esté fresco.
//
// D19: el retiro corre en el mismo tick y después del movimiento, con su propio DbContext. Así, en una
// réplica, el movimiento y el retiro de un mismo archivo nunca corren a la vez (FileResource no tiene
// token de concurrencia); la carrera entre réplicas la cubre PaymentProofDetachProcessor. Cada uno va
// en su propio try: un tick fallido del movimiento no frena el retiro, ni al revés.
internal sealed partial class PaymentProofMoveWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PaymentProofMoveWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof move tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof detach tick failed.")]
    private static partial void LogDetachTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await using (var moveScope = scopeFactory.CreateAsyncScope())
                {
                    await moveScope.ServiceProvider.GetRequiredService<IPaymentProofMoveProcessor>()
                        .ProcessPendingAsync(stoppingToken);
                }
            }
            // Sólo el apagado del host detiene el worker. Otra cancelación (un timeout de R2 que llega
            // como TaskCanceledException) es un tick fallido más: se registra y el siguiente reintenta.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }

            try
            {
                await using var detachScope = scopeFactory.CreateAsyncScope();
                await detachScope.ServiceProvider.GetRequiredService<IPaymentProofDetachProcessor>()
                    .ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogDetachTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
