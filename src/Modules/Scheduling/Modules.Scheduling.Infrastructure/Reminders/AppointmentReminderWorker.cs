using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Scheduling.Infrastructure.Reminders;

/// <summary>
/// Despierta cada <see cref="SchedulingWorkerOptions.ReminderPollSeconds"/> y corre una pasada de <see cref="AppointmentReminderProcessor"/>
/// (calco de <c>WebhookDeliveryWorker</c> y <c>ServiceContractsReminderWorker</c>). Varios pods pueden correrlo a la vez: el reclamo de cada
/// cita da un solo ganador. Sin estado propio, que es lo que el inventario de réplicas pide a un hosted service.
/// </summary>
internal sealed partial class AppointmentReminderWorker(
    AppointmentReminderProcessor processor,
    IOptions<SchedulingWorkerOptions> options,
    ILogger<AppointmentReminderWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Appointment reminder tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.ReminderPollSeconds)));
        // La primera pasada espera un tick, como los demás barredores: al arrancar el host las migraciones todavía pueden estar corriendo.
        // Sólo el apagado sale del bucle; cualquier otra falla se loguea y sigue.
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await processor.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
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

/// <summary>Opciones de los barredores de la Agenda (<c>Scheduling:Workers</c>).</summary>
public sealed class SchedulingWorkerOptions
{
    public const string SectionName = "Scheduling:Workers";

    /// <summary>Cada cuántos segundos mira el barredor del recordatorio si hay citas cuyo aviso ya cayó en su ventana (mínimo 5).</summary>
    public int ReminderPollSeconds { get; set; } = 60;
}
