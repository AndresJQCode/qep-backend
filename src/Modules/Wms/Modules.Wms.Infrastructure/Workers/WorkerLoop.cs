
namespace Modules.Wms.Infrastructure.Workers;

/// <summary>El ciclo común de los workers periódicos de WMS: espera de arranque, primera pasada ya y luego cada periodo. Sin la espera
/// de arranque corta, un periodo largo con un despliegue por commit reiniciaría el pod antes de que el temporizador cumpliera y la
/// pasada no correría nunca. Un fallo de la pasada se registra y el ciclo sigue; sólo la cancelación lo detiene.</summary>
internal static class WorkerLoop
{
    public static async Task RunAsync(
        TimeSpan startupDelay,
        TimeSpan period,
        Func<CancellationToken, Task> tick,
        Action<Exception> onFailure,
        CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(period);
        do
        {
            try
            {
                await tick(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                onFailure(exception);
            }
        }
        while (await WaitNextAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
