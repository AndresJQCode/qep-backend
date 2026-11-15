using Bootstrapper.Valkey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bootstrapper.Observability;

/// <summary>
/// Observabilidad fase 2: cada 15 s lee las seis colas (<see cref="QueueNames.All"/>) y se las pasa a <see cref="QueueGauges"/>.
/// Lee una por una, nunca en paralelo, y cada una en su propio scope: <see cref="QueueMetricsSource"/> usa la conexión del
/// <c>MessagingDbContext</c> del scope, y una lectura que falla a medias (tope de 5 s, conexión caída) no puede dejar rota la conexión
/// de la siguiente. Una cola que falla se loguea y se salta: su lectura vieja caduca sola a los 60 s y las demás siguen.
///
/// Con varios pods sólo muestrea el líder (<see cref="QueueSamplerLeadership"/>, spec 2026-11-13 D-7): la vuelta que no
/// lidera se salta entera. Con el área de timbres apagada o Valkey caído, todos lideran, como antes. <c>internal</c> porque
/// el líder es un tipo interno de Bootstrapper; sólo lo registra <c>AddQepPlatform</c>.
/// </summary>
internal sealed partial class QueueSamplerWorker(
    IServiceScopeFactory scopeFactory,
    QueueGauges gauges,
    QueueSamplerLeadership leadership,
    ILogger<QueueSamplerWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    // Sólo el nombre de la cola y el tipo de la excepción: el mensaje de Npgsql puede traer SQL o el host.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Queue sampling failed for {Queue}: {ExceptionType}.")]
    private static partial void LogReadFailed(ILogger logger, string queue, string exceptionType);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    /// <summary>Una vuelta del bucle: muestrea sólo si este pod lidera. Internal para las pruebas.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (await leadership.TryLeadAsync(cancellationToken))
        {
            await SampleAsync(cancellationToken);
        }
    }

    /// <summary>Una vuelta: internal para las pruebas, sin el <see cref="PeriodicTimer"/> de por medio. No lanza por una cola que falla.</summary>
    internal async Task SampleAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in QueueNames.All)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var source = scope.ServiceProvider.GetRequiredService<IQueueMetricsSource>();
                gauges.Record(await source.ReadAsync(queue, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogReadFailed(logger, queue, exception.GetType().Name);
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
