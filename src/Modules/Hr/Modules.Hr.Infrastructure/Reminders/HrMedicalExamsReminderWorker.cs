using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Hr.Application.BirthdayNotices;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure.Reminders;

/// <summary>
/// Wakes up every <c>Hr:SweepMinutes</c> (same shape as <c>ServiceContractsReminderWorker</c>) and runs the daily run of every tenant
/// with employees (spec 2026-11-23 §9.1). One scope per tenant: the <c>DbContext</c> of one does not drag the tracker of the previous
/// one, and a tenant that fails is logged and does not stop the rest. Whether the tenant is active, has the module and has already
/// run today is decided by the processor.
/// </summary>
internal sealed partial class HrMedicalExamsReminderWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<HrOptions> options,
    ILogger<HrMedicalExamsReminderWorker> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Medical exams reminder tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Medical exams reminder run failed for tenant {TenantId}.")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Birthday notices run failed for tenant {TenantId}.")]
    private static partial void LogBirthdaysFailed(ILogger logger, Exception exception, Guid tenantId);

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
                    await ProcessBirthdaysAsync(tenantId, stoppingToken);
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

    // The tenants with some employee: one without employees has nothing to announce.
    private async Task<IReadOnlyList<Guid>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HrDbContext>().Employees
            .AsNoTracking()
            .Select(employee => employee.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    // The birthday run of the same tenant (spec 2026-11-26 §5.2), in its own scope and try/catch: a failure in one run never stops
    // the other. A tenant without a birthday setting exits at its first query.
    private async Task ProcessBirthdaysAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IHrBirthdayNoticesProcessor>().ProcessTenantAsync(tenantId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogBirthdaysFailed(logger, exception, tenantId);
        }
    }

    private async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IHrMedicalExamsReminderProcessor>().ProcessTenantAsync(tenantId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTenantFailed(logger, exception, tenantId);
        }
    }
}
