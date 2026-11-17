using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;

namespace Modules.Ai.Infrastructure;

/// <summary>Spec 2026-10-29 §6.3 y D-A20: el worker de modelo, con defaults en código como <c>Automations:Workers</c>.</summary>
public sealed class AiWorkerOptions
{
    public const string SectionName = "Ai:Workers";

    /// <summary>El tick de la cola: 1 s, como el inbox de Automations.</summary>
    public int PollSeconds { get; set; } = 1;

    /// <summary>Trabajos por pasada; cada uno se reclama justo antes de correrlo, para que empiece con su lease entero.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>D-A20: el lease de un trabajo, mayor que su tope de 120 s.</summary>
    public int LeaseMinutes { get; set; } = 3;

    /// <summary>D-A14: cuánto espera un trabajo nuevo de una sesión para juntar una ráfaga del cliente.</summary>
    public int DebounceSeconds { get; set; } = 2;

    /// <summary>D-A19: horas sin entrante tras las que una sesión está abandonada.</summary>
    public int IdleHours { get; set; } = 24;

    /// <summary>D-A19: cada cuánto corre el barrido de abandonadas.</summary>
    public int IdleSweepMinutes { get; set; } = 10;

    /// <summary>§6.8: cada cuánto corre el barrido de tenants con el módulo apagado.</summary>
    public int ModuleSweepSeconds { get; set; } = 60;

    /// <summary>Plan 2026-11-12, Tarea 7: apaga los dos workers (cola y modelo). Las pruebas que siembran filas a mano lo apagan para que no
    /// les ganen la carrera; en producción queda prendido.</summary>
    public bool Enabled { get; set; } = true;

    public AiSessionTimings ToTimings() =>
        new(TimeSpan.FromSeconds(Math.Max(0, DebounceSeconds)), TimeSpan.FromHours(Math.Max(1, IdleHours)));
}

/// <summary>
/// Spec 2026-10-29 §6.3: vacía <c>ai.model_jobs</c>. Cada trabajo en su scope: se reclama (<c>SKIP LOCKED</c>), se corre con el modelo fuera de
/// toda transacción y se cierra con un guardado corto. Corre los del playground (§7), los de las sesiones reales (§6.3) y los del copiloto (spec 2026-11-17).
/// </summary>
internal sealed partial class AiModelWorker(
    IServiceScopeFactory scopeFactory, IOptions<AiWorkerOptions> options, ILogger<AiModelWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "AI model worker tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    // Sin texto ni datos del cliente: sólo ids. El trabajo se retoma cuando vence su lease.
    [LoggerMessage(Level = LogLevel.Error, Message = "AI model job {JobId} of tenant {TenantId} threw on attempt {Attempts}; it is retried when its lease expires.")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, Guid tenantId, Guid jobId, int attempts);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
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
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Una pasada: hasta <c>BatchSize</c> trabajos. Internal para las pruebas.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        var lease = TimeSpan.FromMinutes(Math.Max(1, options.Value.LeaseMinutes));
        for (var processed = 0; processed < Math.Max(1, options.Value.BatchSize); processed++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            var job = await scope.ServiceProvider.GetRequiredService<IAiModelJobs>().ClaimAsync(now, lease, cancellationToken);
            if (job is null)
            {
                return;
            }

            try
            {
                if (job.CopilotThreadId is not null)
                {
                    // Spec 2026-11-17 (copiloto) §7: el mismo worker, otro procesador.
                    await scope.ServiceProvider.GetRequiredService<CopilotJobProcessor>().ProcessAsync(job, cancellationToken);
                }
                else if (job.TestId is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<AgentTestJobProcessor>().ProcessAsync(job, cancellationToken);
                }
                else
                {
                    await scope.ServiceProvider.GetRequiredService<AgentSessionJobProcessor>().ProcessAsync(job, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogJobFailed(logger, exception, job.TenantId, job.Id, job.Attempts);
            }
        }
    }
}
