using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Infrastructure.Persistence;

namespace Modules.ServiceContracts.Infrastructure.Reminders;

/// <summary>
/// Despierta cada <c>ServiceContracts:SweepMinutes</c> (copia de <c>QuotationExpirationWorker</c>) y corre la corrida diaria de cada tenant
/// con servicios de cobro (spec §3.3). Un scope por tenant: el <c>DbContext</c> de uno no arrastra el tracker del anterior, y un
/// tenant que falla se registra y no frena a los demás.
/// </summary>
internal sealed partial class ServiceContractsReminderWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ServiceContractsOptions> options,
    ILogger<ServiceContractsReminderWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Billing reminder tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing reminder run failed for tenant {TenantId}.")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.SweepMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var tenantId in await ListTenantsAsync(stoppingToken))
                {
                    await ProcessTenantAsync(tenantId, stoppingToken);
                }
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
    }

    /// <summary>
    /// Los tenants con algún servicio de cobro: un tenant sin servicios no tiene nada que generar, avisar ni reportar. No se usa
    /// <c>IActiveTenantsQuery</c>, que lista los tenants de un usuario, no los de la plataforma. Que el tenant esté <c>Active</c> y el
    /// módulo prendido lo revisa el procesador.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Services
            .AsNoTracking()
            .Select(service => service.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IServiceContractsReminderProcessor>().ProcessTenantAsync(tenantId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTenantFailed(logger, exception, tenantId);
        }
    }
}
