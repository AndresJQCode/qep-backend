using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Loans.Application;
using Modules.Loans.Domain;
using Modules.Loans.Infrastructure.Persistence;

namespace Modules.Loans.Infrastructure.Generation;

/// <summary>
/// Despierta cada <c>Loans:SweepMinutes</c> (copia de <c>ServiceContractsReminderWorker</c>) y corre la generación diaria de cada
/// tenant con préstamos activos y capital pendiente. Un scope por tenant: el <c>DbContext</c> de uno no arrastra el tracker del
/// anterior, y un tenant que falla se registra y no frena a los demás.
/// </summary>
internal sealed partial class LoansGenerationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<LoansOptions> options,
    ILogger<LoansGenerationWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Loan generation tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Loan generation run failed for tenant {TenantId}.")]
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
    /// Los tenants con algún préstamo activo y con capital pendiente: sin eso no hay período que convertir en cuota. Que el tenant
    /// esté <c>Active</c> y el módulo prendido lo revisa el procesador.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LoansDbContext>().Loans
            .AsNoTracking()
            .Where(loan => loan.Status == LoanStatus.Active && loan.OutstandingPrincipal > 0)
            .Select(loan => loan.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ILoansGenerationProcessor>().ProcessTenantAsync(tenantId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTenantFailed(logger, exception, tenantId);
        }
    }
}
